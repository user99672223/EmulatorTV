import Foundation
import os

final class EmulatorRunner: ObservableObject {
    static let shared = EmulatorRunner()

    @Published private(set) var isRunning = false
    /// True from the moment Start is pressed until the emulator thread is launched.
    /// The Metal view is mounted during this window so the CAMetalLayer exists before
    /// the core asks for it.
    @Published private(set) var isPreparing = false
    @Published private(set) var lastMessage: String?

    /// Set to true while either preparing or running, so the UI shows the Metal view.
    var showsOutput: Bool { isPreparing || isRunning }

    private var pendingArgs: [String]?

    private init() {}

    /// Boots a title. The core's main loop blocks for the lifetime of the game,
    /// so it gets its own thread with a large stack; the UI thread stays free to
    /// service the SDL main-thread dispatcher the core installs on Apple targets.
    func start(game: URL, applicationPoolMiB: Int, appletPoolMiB: Int = 16, asBlockAlignMiB: Int = 64, dramMiB: Int = 2048) {
        guard !isRunning, !isPreparing else { return }
        lastMessage = nil

        // A debugger may have attached since launch, so this is re-read rather than
        // cached. Starting without it is a guaranteed kill at the first guest
        // instruction, and the kill is silent -- better to say so than to crash.
        JitStatus.report("at game start")

        guard JitStatus.isDebugged else {
            lastMessage = """
                JIT is not enabled for this launch (CS_DEBUGGED is not set).
                The emulator would be killed by the kernel at the first guest                 instruction. Attach the debugger, then press Start again.
                """
            return
        }

        var args: [String] = [game.path]

        args += ["--graphics-backend", "Vulkan"]

        // HostMappedUnsafe is what the iOS build ships with; it skips the guest
        // address masking that HostMapped does on every access.
        args += ["--memory-manager-mode", "HostMappedUnsafe"]

        // Render at the game's native 720p and let the TV scale. Anything larger
        // costs memory for no visible gain on a title that is 720p on hardware.
        args += ["--exclusive-fullscreen", "true"]
        args += ["--exclusive-fullscreen-width", "1280"]
        args += ["--exclusive-fullscreen-height", "720"]
        args += ["--aspect-ratio", "Fixed16x9"]

        args += ["--system-language", "AmericanEnglish"]
        args += ["--system-region", "USA"]

        // Rocket League's online services are gone; stubbing the ones the core
        // does not implement avoids a hard stop on an unimplemented call.
        args += ["--ignore-missing-services"]

        if applicationPoolMiB > 0 {
            args += ["--application-pool-mib", String(applicationPoolMiB)]
        }

        // The stock arrangement parks 507 MiB in the applet pool for system applets
        // layered over a running game -- the home menu overlay, the software
        // keyboard. None of that happens here, and inside a 2048 MiB DRAM it was
        // 507 MiB the game could not have.
        //
        // Measured: at an application pool of 1280 MiB the guest is told it has
        // 1196 MiB, reserves 1048 MiB of fixed GPU blocks (targets, textures,
        // buffers, shaders, command buffers, queries), and dies initialising the
        // texture sampler pools with "Pure virtual function called!" -- an
        // allocation returned null and the object was used anyway.
        //
        // Handing the applet pool's space to the application pool reaches the main
        // menu on the same 2048 MiB of DRAM. Note the service pool cannot absorb
        // this instead: squeezed to its 64 MiB floor the loader dies before the
        // game starts.
        if appletPoolMiB > 0 {
            args += ["--applet-pool-mib", String(appletPoolMiB)]
        }

        // The emulated DRAM is reserved as one contiguous block of address space,
        // and it is the largest single thing the emulator spends its budget on.
        //
        // Measured: the process has about 7100 MiB of address space to grow into
        // after launch (453478 MiB at launch, allocations refused at 460600), and
        // the span between its lowest and highest mapping never changes -- so this
        // is a budget, not a question of where things land. At 3072 MiB the DRAM
        // block alone took nearly half of it, and guest memory ran out after
        // 1019 MiB of partitions while only 325 MiB was resident.
        //
        // 2048 hands a full gigabyte of that back. The guest pools are sized from
        // this, so it also caps how much the game can be told it has.
        if dramMiB > 0 {
            args += ["--dram-mib", String(dramMiB)]
        }

        // Without an --input-id-N the core's Load() finds no configured player and
        // returns before it creates a window or an emulation context, so nothing
        // boots and main_ryujinx_sdl just returns 0. The id must match the pointer
        // ControllerManager hands to attach_gamepad.
        args += ["--input-id-1", ControllerManager.gamepadIdString]

        // Host address space is the scarcest thing on this device, and a third of what
        // the guest gets is lost to allocation granularity.
        //
        // Measured at the refusal: of the ~6 GiB hole the guest has to work in, 4000 MiB
        // was mapped and 2078 MiB was reserved and never mapped, mostly in 128 MiB
        // pieces. That waste is the partition allocator's block size, which defaults to
        // 256 MiB here.
        //
        // This is an environment variable rather than an argument because the core reads
        // it directly, and it is read lazily when the first partition is created -- well
        // after this point -- so setting it here takes effect.
        if asBlockAlignMiB > 0 {
            setenv("AS_BLOCK_ALIGN_MIB", String(asBlockAlignMiB), 1)
            EmulatorRunner.log("AS_BLOCK_ALIGN_MIB=\(asBlockAlignMiB)")
        }

        args += ["--controller-type-1", "ProController"]

        EmulatorRunner.log("starting: " + args.joined(separator: " "))

        // Do NOT start the emulator yet. The core copies the CAMetalLayer once, early
        // in LoadApplication, and gives up if it is not there; mounting the Metal view
        // only after the emulator starts loses that race and the render thread dies
        // with "No CAMetalLayer set". Store the arguments and wait for the layer.
        pendingArgs = args
        isPreparing = true
        EmulatorRunner.log("prepared, waiting for the Metal layer")
    }

    /// Called by the Metal view once it has handed its layer to the core.
    func layerReady() {
        guard isPreparing, let args = pendingArgs else { return }

        pendingArgs = nil
        isPreparing = false
        isRunning = true

        EmulatorRunner.log("layer handed over, starting emulator")

        let thread = Thread {
            let result = RyujinxBridge.mainRyu(argv: args)
            EmulatorRunner.log("main_ryujinx_sdl returned \(result)")
            DispatchQueue.main.async {
                EmulatorRunner.shared.isRunning = false
                EmulatorRunner.shared.isPreparing = false
                EmulatorRunner.shared.lastMessage = "Emulator exited with code \(result)."
            }
        }
        thread.name = "ryujinx-main"
        thread.stackSize = 16 * 1024 * 1024
        thread.start()
    }

    private static let logger = OSLog(subsystem: "com.melotv.app", category: "emulator")

    static func log(_ message: String) {
        os_log("%{public}s", log: logger, type: .default, "[run] " + message)
    }

    func stop() {
        guard isRunning else { return }
        RyujinxBridge.stopEmulation()
    }
}

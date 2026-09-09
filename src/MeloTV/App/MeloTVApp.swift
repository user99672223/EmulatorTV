import SwiftUI
import os

@main
struct MeloTVApp: App {
    init() {
        // First, so the core's SIGSEGV handler chains back to this one rather than
        // replacing it. A native fault currently leaves nothing in the log at all.
        CrashReporter.install()

        // Says immediately whether JIT execution is legal on this launch. Without
        // CS_DEBUGGED the emulator dies at the first guest instruction with no signal
        // and no log line, so this is the difference between a diagnosable run and a
        // silent return to the home screen.
        JitStatus.report("at launch")

        Paths.ensureDirectories()

        // Every one of these has to be set before initialize(), because the core
        // reads them while starting up. The iOS app sets the same set; this target
        // was setting none of them.
        MeloTVApp.setupEnvironment()

        RyujinxBridge.initialize()

        // Primes the dual-mapped JIT translator. Doing it here rather than at game
        // boot means the log says immediately whether executable memory can be had
        // at all on this device, instead of failing much later inside a game load.
        let dualMapped = RyujinxBridge.initialize_dualmapped()
        MeloTVApp.log("dual-mapped JIT init returned \(dualMapped)")
    }

    var body: some Scene {
        WindowGroup {
            ContentView()
                .onAppear { ControllerManager.shared.begin() }
        }
    }

    private static func setupEnvironment() {
        let vars: [(String, String)] = [
            // MoltenVK tuning, copied from the iOS target.
            ("MVK_USE_METAL_PRIVATE_API", "1"),
            ("MVK_CONFIG_USE_METAL_PRIVATE_API", "1"),
            ("MVK_DEBUG", "0"),
            ("MVK_CONFIG_MAX_ACTIVE_METAL_COMMAND_BUFFERS_PER_QUEUE", "128"),

            ("DOTNET_DefaultStackSize", "200000"),

            // The Apple TV has TXM active, but HAS_TXM=1 routes JIT allocation through
            // BreakpointJIT.framework, which is an iOS-only binary that is not in this
            // bundle and which needs an attached debugger to service its brk traps.
            // Leaving it off keeps the allocator on the mmap path.
            ("HAS_TXM", "0"),

            // Off, deliberately, and this is a reversal.
            //
            // The dual-mapped path exists to obtain executable memory WITHOUT a debugger,
            // which was the situation when it was selected: CS_DEBUGGED was clear and the
            // kernel killed the process at the first guest instruction. That premise no
            // longer holds. JIT is now enabled externally before the game starts, so
            // CS_DEBUGGED is set and CS_HARD/CS_KILL are cleared, and the process may
            // execute unsigned pages the ordinary way.
            //
            // With DUAL_MAPPED_JIT unset the translator uses NoWxCache instead -- the path
            // upstream MeloNX actually ships on iOS, rather than this fork's bespoke
            // dual-mapping. That matters because the dual-mapped dispatch code decodes as
            // correct in every instruction yet never executes: the guest burns a full core
            // without retiring a single instruction or reaching managed code.
            ("DUAL_MAPPED_JIT", "0"),

            // Address space, not resident memory, is the binding constraint here: a
            // 1 GiB + 256 MiB JIT reservation on top of guest DRAM and the
            // host-tracked page table exhausted it, and mmap returned ENOMEM with 2 GB
            // of jetsam headroom still free. These are caches, so a smaller reservation
            // costs eviction churn rather than correctness.
            ("JIT_SHARED_CACHE_MIB", "256"),
            ("JIT_LOCAL_CACHE_MIB", "64"),
        ]

        for (key, value) in vars {
            setenv(key, value, 1)
        }
    }

    private static let logger = OSLog(subsystem: "com.melotv.app", category: "emulator")

    static func log(_ message: String) {
        os_log("%{public}s", log: logger, type: .default, message)
    }
}

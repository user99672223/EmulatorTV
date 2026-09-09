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

            // Back on, and this reverses the earlier reversal for a measured reason.
            //
            // This device permits execute only on pages created executable and never
            // written to: mmap with PROT_READ|PROT_EXEC gives cur=r-x, while mprotect
            // adding execute to a writable page returns success, silently leaves the page
            // r--, and permanently strips execute from its maximum protection. NoWxCache
            // depends on exactly that flip, so it cannot work here no matter how its
            // memory is reserved -- which is what the r--/max=rw- pages were all along.
            //
            // The dual mapping writes through an rw alias and executes through a separate
            // r-x mapping of the same physical pages, so nothing ever needs the forbidden
            // transition.
            //
            // Superseded note from when this was set to 0:
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
            ("DUAL_MAPPED_JIT", "1"),

            // Skip the mach ownership remap for JIT memory.
            //
            // vm_region_64 measured the JIT pages as cur=r-- max=rw- while an ordinary
            // allocation on the same device showed max=rwx, and the remap ran with every
            // mach call succeeding. It is therefore the thing removing execute, not
            // granting it: mach_make_memory_entry_64 cannot hand out VM_PROT_EXECUTE
            // without dynamic-codesigning, and vm_map clamps its requested max to the
            // entry rather than failing. Plain anonymous mmap already carries max=rwx,
            // which is all that is needed now that CS_DEBUGGED is set.
            ("JIT_OWNERSHIP_REMAP", "0"),

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

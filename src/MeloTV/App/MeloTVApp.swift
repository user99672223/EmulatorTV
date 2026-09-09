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

        // Deliberately NOT priming the JIT here any more.
        //
        // It used to call initialize_dualmapped() so the log would say immediately
        // whether executable memory could be had. With HAS_TXM=1 that is now the wrong
        // thing to do at the wrong time: allocation goes through a BRK that an attached
        // debugger has to service, and at launch nothing is attached yet -- the script
        // cannot connect to a process that does not exist. The trap would raise SIGTRAP
        // with no handler and kill the app before the Start button was reachable.
        //
        // Allocation is left to happen at game start, by which point the script is
        // attached and serving. The translator creates the cache lazily anyway, and the
        // signal handler's own code mapping goes through the same allocator, so deferring
        // this defers both.
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

            // Stop the GC reserving a quarter-terabyte of address space.
            //
            // Measured at launch, before the emulator allocates anything at all:
            // 442 GiB of virtual address space across 186 regions, against 92 MiB
            // actually resident. The GC's region heap reserves a very large range
            // up front on 64-bit, which costs nothing on a desktop and is nearly
            // everything here -- allocations start failing at about 460 GiB, so
            // that baseline leaves the emulator only a few GiB to work in, and
            // guest memory runs out of address space long before it runs out of
            // memory.
            //
            // 4 GiB is far more than this process needs; managed heap has stayed
            // around 50 MiB and peaked near 330 MiB across every run so far.
            ("DOTNET_GCRegionRange", "0x100000000"),

            // On, and this reverses the first decision made in this port.
            //
            // It was set to 0 because BreakpointJIT.framework is an iOS-only binary that
            // was not in this bundle. Both halves of that reasoning have now changed: the
            // framework is rebuilt for tvOS from the original's own symbol table and is
            // embedded here, and the mmap path it was avoiding has been measured to be
            // impossible on this device -- every route to executable memory the app can
            // take alone is refused, including a page created PROT_READ|PROT_EXEC and
            // never written through.
            //
            // What does work is asking the debugger: a region it allocates rx executes,
            // confirmed by a BRK returning SIGTRAP/EXC_BREAKPOINT against an rw control
            // that returned EXC_BAD_ACCESS. HAS_TXM=1 routes allocation through the trap
            // stubs that ask it.
            //
            // This makes the attached script load-bearing for the whole session, not just
            // at startup: it has to service every JIT allocation.
            ("HAS_TXM", "1"),

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
            // As small as is still useful, on purpose.
            //
            // These are allocated by the debugger through debugserver's _M packet, which
            // has only ever been verified at 4 KiB. Anything larger tests the size rather
            // than the mechanism: a refusal at 256 MiB would say nothing about whether
            // asking the debugger for JIT memory works at all, and would stop the boot
            // before vm_remap, write visibility through the alias, or guest execution
            // could be observed. 4 MiB still holds thousands of translated functions,
            // which is far more than reaching the entry point needs.
            //
            // The cost of small caches is eviction churn in a long session, not
            // correctness. Raise them once an allocation of this shape is known to work.
            // One translated block per page, and pages here are 16 KiB -- the
            // published addresses came back 0x4000 apart. So these are counts of
            // blocks, not a byte budget: 4 MiB held 256 of them, which the game
            // exhausted in under a minute and then spent its time evicting and
            // retranslating the same code.
            //
            // Each block costs a debugger round trip to publish, so a cache that
            // thrashes does not merely run slowly, it never converges. 64 MiB is
            // 4096 blocks. The earlier caution was about whether a large _M would
            // be granted at all; 4 MiB is granted, and the allocator reports a
            // refusal clearly if this proves too large.
            ("JIT_SHARED_CACHE_MIB", "64"),
            ("JIT_LOCAL_CACHE_MIB", "8"),
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

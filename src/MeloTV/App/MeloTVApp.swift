import SwiftUI
import os

@main
struct MeloTVApp: App {
    init() {
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

            // Selects RX-plus-RW-alias mappings instead of allocating RW and then
            // mprotecting to RX. The latter is what a W^X-enforcing system rejects,
            // and it is used by the JIT cache, the translator AND the SIGSEGV handler
            // that host-tracked memory depends on.
            ("DUAL_MAPPED_JIT", "1"),
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

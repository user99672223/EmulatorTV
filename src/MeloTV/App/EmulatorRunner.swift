import Foundation
import os

final class EmulatorRunner: ObservableObject {
    static let shared = EmulatorRunner()

    @Published private(set) var isRunning = false
    @Published private(set) var lastMessage: String?

    private init() {}

    /// Boots a title. The core's main loop blocks for the lifetime of the game,
    /// so it gets its own thread with a large stack; the UI thread stays free to
    /// service the SDL main-thread dispatcher the core installs on Apple targets.
    func start(game: URL, applicationPoolMiB: Int) {
        guard !isRunning else { return }
        isRunning = true
        lastMessage = nil

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

        // Without an --input-id-N the core's Load() finds no configured player and
        // returns before it creates a window or an emulation context, so nothing
        // boots and main_ryujinx_sdl just returns 0. The id must match the pointer
        // ControllerManager hands to attach_gamepad.
        args += ["--input-id-1", ControllerManager.gamepadIdString]
        args += ["--controller-type-1", "ProController"]

        EmulatorRunner.log("starting: " + args.joined(separator: " "))

        let thread = Thread {
            let result = RyujinxBridge.mainRyu(argv: args)
            EmulatorRunner.log("main_ryujinx_sdl returned \(result)")
            DispatchQueue.main.async {
                EmulatorRunner.shared.isRunning = false
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

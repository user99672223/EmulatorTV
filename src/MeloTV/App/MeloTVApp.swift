import SwiftUI

@main
struct MeloTVApp: App {
    init() {
        Paths.ensureDirectories()
        RyujinxBridge.initialize()
    }

    var body: some Scene {
        WindowGroup {
            ContentView()
                .onAppear { ControllerManager.shared.begin() }
        }
    }
}

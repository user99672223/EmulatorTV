import Foundation

// tvOS gives an app exactly two writable directories, Library/Caches and tmp,
// and both are purgeable. Documents and Application Support are sandbox-denied
// on real hardware even though they work in the Simulator. AppDataManager on the
// C# side was patched to use Library/Caches for the same reason, so these two
// have to agree or the emulator will look for keys where nothing was put.
enum Paths {
    static var base: URL {
        FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
    }

    /// Where the core expects prod.keys / title.keys (AppDataManager KeysDir).
    static var system: URL { base.appendingPathComponent("system") }

    static var games: URL { base.appendingPathComponent("games") }

    static var prodKeys: URL { system.appendingPathComponent("prod.keys") }

    static func ensureDirectories() {
        for dir in [system, games] {
            try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        }
    }
}

import Foundation

struct StoredFile: Identifiable, Hashable {
    let url: URL
    var id: String { url.path }
    var name: String { url.lastPathComponent }
    var isGame: Bool { ["nsp", "xci", "nca", "nro"].contains(url.pathExtension.lowercased()) }
    var sizeBytes: Int64 {
        let attrs = try? FileManager.default.attributesOfItem(atPath: url.path)
        return (attrs?[.size] as? Int64) ?? 0
    }
    var sizeText: String {
        ByteCountFormatter.string(fromByteCount: sizeBytes, countStyle: .file)
    }
}

/// Lists whatever the app actually has on disk and reports what is missing,
/// rather than letting the emulator fail somewhere deeper with a worse message.
final class GameLibrary: ObservableObject {
    @Published private(set) var files: [StoredFile] = []
    @Published private(set) var firmwareVersion: String?
    @Published private(set) var scanError: String?

    var games: [StoredFile] { files.filter(\.isGame) }
    var hasKeys: Bool { FileManager.default.fileExists(atPath: Paths.prodKeys.path) }
    var hasFirmware: Bool { !(firmwareVersion ?? "").isEmpty }

    /// Without these a boot cannot even be attempted.
    var blocking: [String] {
        var m: [String] = []
        if !hasKeys { m.append("prod.keys (put it in Library/Caches/system/)") }
        if games.isEmpty { m.append("a game (.nsp / .xci / .nca)") }
        return m
    }

    /// Firmware is deliberately NOT blocking. It only reads as installed once its
    /// NCAs have been registered by install_firmware, so gating Start on it made the
    /// button impossible to enable -- and a disabled button on tvOS cannot even take
    /// focus, which left the remote with nowhere to go.
    var warnings: [String] {
        hasFirmware ? [] : ["Switch firmware is not installed; most games will not boot"]
    }

    /// Firmware archives sitting in the data directory, ready to be installed.
    var installableFirmware: [StoredFile] {
        files.filter { ["zip", "xci"].contains($0.url.pathExtension.lowercased()) }
    }

    func installFirmware(_ file: StoredFile) -> String {
        let result = RyujinxBridge.installFirmware(at: file.url.path)
        refresh()
        return result.isError ? "Firmware install failed: \(result.string)" : "Installed firmware \(result.string)"
    }

    func refresh() {
        Paths.ensureDirectories()
        var found: [StoredFile] = []
        let fm = FileManager.default

        if let walker = fm.enumerator(at: Paths.base,
                                      includingPropertiesForKeys: [.isRegularFileKey],
                                      options: [.skipsHiddenFiles]) {
            for case let url as URL in walker {
                if (try? url.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) == true {
                    found.append(StoredFile(url: url))
                }
                if found.count > 500 { break }   // a purged cache can still hold junk
            }
        }

        files = found.sorted { $0.name.lowercased() < $1.name.lowercased() }
        if let raw = SN_installed_firmware_version() {
            let v = String(cString: raw)
            firmwareVersion = v.isEmpty ? nil : v
        } else {
            firmwareVersion = nil
        }
        scanError = nil
    }
}

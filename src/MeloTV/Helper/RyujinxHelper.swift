import Foundation
import UIKit

// A source replacement for the prebuilt RyujinxHelper.framework, which only
// exists as an iOS-platform binary with no source anywhere in the tree.
//
// The emulator core calls six of these through
// DllImport("RyujinxHelper.framework/RyujinxHelper"), so both the framework name
// and the exported C symbol names have to match the originals exactly. Nothing
// here is clever: it is a callback registry plus two UIKit alerts, and every
// API it touches exists on tvOS.

// MARK: - Callback registry

private final class Registry {
    static let shared = Registry()

    private let lock = NSLock()
    private var simple: [String: () -> Void] = [:]
    private var withData: [String: (UnsafeRawPointer?, Int) -> Void] = [:]

    func register(_ id: String, _ cb: @escaping () -> Void) {
        lock.lock(); defer { lock.unlock() }
        simple[id] = cb
    }

    func registerData(_ id: String, _ cb: @escaping (UnsafeRawPointer?, Int) -> Void) {
        lock.lock(); defer { lock.unlock() }
        withData[id] = cb
    }

    func fire(_ id: String) {
        lock.lock(); let cb = simple[id]; lock.unlock()
        guard let cb else { return }
        DispatchQueue.main.async(execute: cb)
    }

    func fireData(_ id: String, _ data: UnsafeRawPointer?, _ len: Int) {
        lock.lock(); let cb = withData[id]; lock.unlock()
        guard let cb else { return }

        // The caller owns `data` and may free it the moment this returns, so the
        // payload is copied before hopping to the main queue.
        var copy: [UInt8] = []
        if let data, len > 0 {
            copy = [UInt8](UnsafeRawBufferPointer(start: data, count: len))
        }
        DispatchQueue.main.async {
            copy.withUnsafeBytes { cb($0.baseAddress, len) }
        }
    }
}

/// Register a handler for an event raised by the emulator core.
public func MeloRegisterCallback(_ id: String, _ cb: @escaping () -> Void) {
    Registry.shared.register(id, cb)
}

/// Register a handler that also receives a payload (rumble, progress, ...).
public func MeloRegisterCallbackWithData(_ id: String, _ cb: @escaping (UnsafeRawPointer?, Int) -> Void) {
    Registry.shared.registerData(id, cb)
}

private func str(_ p: UnsafePointer<CChar>?) -> String {
    guard let p else { return "" }
    return String(cString: p)
}

// MARK: - C ABI consumed by the emulator core

@_cdecl("TriggerCallback")
public func TriggerCallback(_ identifier: UnsafePointer<CChar>?) {
    Registry.shared.fire(str(identifier))
}

@_cdecl("TriggerCallbackWithData")
public func TriggerCallbackWithData(_ identifier: UnsafePointer<CChar>?,
                                    _ data: UnsafeMutableRawPointer?,
                                    _ dataLength: Int) {
    Registry.shared.fireData(str(identifier), UnsafeRawPointer(data), dataLength)
}

@_cdecl("RegisterCallback")
public func RegisterCallback(_ identifier: UnsafePointer<CChar>?,
                             _ callback: (@convention(c) (UnsafeMutableRawPointer?) -> Void)?) {
    guard let callback else { return }
    Registry.shared.register(str(identifier)) { callback(nil) }
}

@_cdecl("RegisterCallbackWithData")
public func RegisterCallbackWithData(_ identifier: UnsafePointer<CChar>?,
                                     _ callback: (@convention(c) (UnsafeMutableRawPointer?, Int) -> Void)?) {
    guard let callback else { return }
    Registry.shared.registerData(str(identifier)) { ptr, len in
        callback(UnsafeMutableRawPointer(mutating: ptr), len)
    }
}

// MARK: - UIKit surface

private func topViewController() -> UIViewController? {
    let scene = UIApplication.shared.connectedScenes
        .compactMap { $0 as? UIWindowScene }
        .first { $0.activationState == .foregroundActive }
        ?? UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first

    var vc = scene?.windows.first(where: { $0.isKeyWindow })?.rootViewController
        ?? scene?.windows.first?.rootViewController
    while let presented = vc?.presentedViewController { vc = presented }
    return vc
}

@_cdecl("getMainDeviceWindowScene")
public func getMainDeviceWindowScene() -> UnsafeMutableRawPointer? {
    guard let scene = UIApplication.shared.connectedScenes.compactMap({ $0 as? UIWindowScene }).first else {
        return nil
    }
    return Unmanaged.passUnretained(scene).toOpaque()
}

// `showCancel` arrives as a 4-byte value: .NET marshals bool as UnmanagedType.Bool
// by default, so this is deliberately Int32 rather than Bool.
@_cdecl("showAlert")
public func showAlert(_ title: UnsafePointer<CChar>?,
                      _ message: UnsafePointer<CChar>?,
                      _ showCancel: Int32) {
    let t = str(title), m = str(message), cancel = showCancel != 0
    DispatchQueue.main.async {
        guard let host = topViewController() else { return }
        let a = UIAlertController(title: t, message: m, preferredStyle: .alert)
        a.addAction(UIAlertAction(title: "OK", style: .default))
        if cancel { a.addAction(UIAlertAction(title: "Cancel", style: .cancel)) }
        host.present(a, animated: true)
    }
}

// MARK: - Software keyboard applet

private let keyboardLock = NSLock()
private var keyboardText: String?

@_cdecl("showKeyboardAlert")
public func showKeyboardAlert(_ title: UnsafePointer<CChar>?,
                              _ message: UnsafePointer<CChar>?,
                              _ placeholder: UnsafePointer<CChar>?) {
    let t = str(title), m = str(message), p = str(placeholder)
    DispatchQueue.main.async {
        guard let host = topViewController() else { return }
        let a = UIAlertController(title: t, message: m, preferredStyle: .alert)
        a.addTextField { $0.placeholder = p }
        a.addAction(UIAlertAction(title: "OK", style: .default) { _ in
            keyboardLock.lock()
            keyboardText = a.textFields?.first?.text ?? ""
            keyboardLock.unlock()
        })
        a.addAction(UIAlertAction(title: "Cancel", style: .cancel) { _ in
            keyboardLock.lock(); keyboardText = ""; keyboardLock.unlock()
        })
        host.present(a, animated: true)
    }
}

/// Returns a heap copy the caller is expected to free, or NULL while the user is
/// still typing. Returning NULL rather than an empty string is what lets the
/// core distinguish "not finished" from "entered nothing".
@_cdecl("getKeyboardInput")
public func getKeyboardInput() -> UnsafeMutablePointer<CChar>? {
    keyboardLock.lock(); defer { keyboardLock.unlock() }
    guard let text = keyboardText else { return nil }
    return strdup(text)
}

@_cdecl("clearKeyboardInput")
public func clearKeyboardInput() {
    keyboardLock.lock(); keyboardText = nil; keyboardLock.unlock()
}

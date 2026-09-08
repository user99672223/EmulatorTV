import Foundation
import Darwin
import os

/// Reads this process's own code-signing status.
///
/// Executing JIT-compiled code on this device is only legal while the kernel has
/// CS_DEBUGGED set on the task. Without it, the first instruction fetch from the
/// translator's anonymous memory kills the process at the Mach level -- before the
/// BSD signal layer, which is why no crash handler ever fires and the app simply
/// disappears back to the home screen.
///
/// That flag is set by an attached debugger, not by anything this app can do. So the
/// only useful thing the app can do is say whether it is set, and refuse to walk into
/// the kill if it is not.
enum JitStatus {
    private static let log = OSLog(subsystem: "com.melotv.app", category: "jit")

    private static let CS_OPS_STATUS: UInt32 = 0

    private static let flagNames: [(UInt32, String)] = [
        (0x00000001, "CS_VALID"),
        (0x00000004, "CS_GET_TASK_ALLOW"),
        (0x00000100, "CS_HARD"),
        (0x00000200, "CS_KILL"),
        (0x00002000, "CS_REQUIRE_LV"),
        (0x10000000, "CS_DEBUGGED"),
        (0x20000000, "CS_SIGNED"),
        (0x40000000, "CS_DEV_CODE"),
    ]

    /// Nil when csops could not be called at all, which is itself worth reporting.
    static func flags() -> UInt32? {
        typealias CsopsFn = @convention(c) (pid_t, UInt32, UnsafeMutableRawPointer?, Int) -> Int32

        guard let sym = dlsym(UnsafeMutableRawPointer(bitPattern: -2), "csops") else {
            return nil
        }

        let csops = unsafeBitCast(sym, to: CsopsFn.self)

        var status: UInt32 = 0
        let result = withUnsafeMutableBytes(of: &status) { buffer -> Int32 in
            csops(getpid(), CS_OPS_STATUS, buffer.baseAddress, buffer.count)
        }

        return result == 0 ? status : nil
    }

    static var isDebugged: Bool {
        guard let value = flags() else { return false }
        return value & 0x10000000 != 0
    }

    static func describe() -> String {
        guard let value = flags() else {
            return "code-signing status unavailable (csops failed)"
        }

        let set = flagNames.filter { value & $0.0 != 0 }.map(\.1).joined(separator: " ")
        let verdict = value & 0x10000000 != 0
            ? "CS_DEBUGGED IS SET -- JIT execution permitted"
            : "CS_DEBUGGED NOT SET -- executing JIT code will be killed by the kernel"

        return String(format: "flags=0x%08X [%@] %@", value, set, verdict)
    }

    /// Logs the status. Called at launch and again when a game is started, because
    /// a debugger attached between the two would change the answer.
    static func report(_ moment: String) {
        os_log("%{public}s", log: log, type: .default, "[jit] \(moment): \(describe())")
    }
}

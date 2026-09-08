import Foundation
import Darwin
import os

/// Turns a hard crash into a readable log line.
///
/// The emulator now dies with a native fault rather than a managed exception, and a
/// corpse carries nothing into the unified log -- the last thing seen is whatever the
/// app printed before it died. This installs handlers that record the signal, the
/// faulting address and a backtrace before letting the process die normally.
///
/// Installing this BEFORE the core matters. Ryujinx registers its own SIGSEGV handler
/// for host-tracked guest memory and stores whatever was installed previously in
/// SignalHandlerConfig.UnixOldSigaction, chaining to it for faults it does not own. So
/// installing first makes this the fallback rather than replacing the handler the
/// memory manager depends on.
enum CrashReporter {
    private static let log = OSLog(subsystem: "com.melotv.app", category: "crash")

    private static let signals: [(Int32, String)] = [
        (SIGSEGV, "SIGSEGV"),   // Ryujinx owns this one; we are its fallback
        (SIGBUS, "SIGBUS"),
        (SIGILL, "SIGILL"),     // what an unexecutable JIT page would raise
        (SIGTRAP, "SIGTRAP"),   // what a brk instruction with no debugger raises
        (SIGFPE, "SIGFPE"),
        (SIGABRT, "SIGABRT"),
    ]

    static func install() {
        for (number, _) in signals {
            var action = sigaction()
            action.__sigaction_u.__sa_sigaction = { signalNumber, info, _ in
                CrashReporter.report(signalNumber, info)
            }
            action.sa_flags = Int32(SA_SIGINFO)
            sigemptyset(&action.sa_mask)
            sigaction(number, &action, nil)
        }

        os_log("%{public}s", log: log, type: .default, "[crash] handlers installed")
    }

    private static func name(for signalNumber: Int32) -> String {
        signals.first { $0.0 == signalNumber }?.1 ?? "signal \(signalNumber)"
    }

    private static func report(_ signalNumber: Int32, _ info: UnsafeMutablePointer<siginfo_t>?) {
        let address = info?.pointee.si_addr.map { UInt(bitPattern: $0) } ?? 0

        os_log("%{public}s", log: log, type: .fault,
               "[crash] \(name(for: signalNumber)) code=\(info?.pointee.si_code ?? 0) at 0x\(String(address, radix: 16))")

        // dladdr gives image + offset per frame, which is directly comparable against
        // the symbols kept in the shipped dylib.
        var frames = [UnsafeMutableRawPointer?](repeating: nil, count: 40)
        let count = frames.withUnsafeMutableBufferPointer { buffer -> Int32 in
            backtrace(buffer.baseAddress, Int32(buffer.count))
        }

        for index in 0..<Int(count) {
            guard let frame = frames[index] else { continue }

            var dlinfo = Dl_info()
            if dladdr(frame, &dlinfo) != 0, let fname = dlinfo.dli_fname {
                let image = String(cString: fname).split(separator: "/").last.map(String.init) ?? "?"
                let slide = UInt(bitPattern: frame) - UInt(bitPattern: dlinfo.dli_fbase)
                os_log("%{public}s", log: log, type: .fault,
                       "[crash]   \(index): \(image) + 0x\(String(slide, radix: 16))")
            } else {
                os_log("%{public}s", log: log, type: .fault,
                       "[crash]   \(index): 0x\(String(UInt(bitPattern: frame), radix: 16))")
            }
        }

        // Die the way we would have without this handler, so the corpse is unchanged.
        var restore = sigaction()
        restore.__sigaction_u.__sa_handler = SIG_DFL
        sigemptyset(&restore.sa_mask)
        sigaction(signalNumber, &restore, nil)
        raise(signalNumber)
    }
}

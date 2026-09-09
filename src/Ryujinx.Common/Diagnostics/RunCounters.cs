using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace Ryujinx.Common.Diagnostics
{
    /// <summary>
    /// Counters for "is anything actually happening?".
    ///
    /// A black screen with a live process is ambiguous in a way the log cannot resolve
    /// on its own: implemented service calls log at Debug (off by default), and a guest
    /// spinning inside code it has already translated logs nothing at all. So the
    /// emulator can be wedged and silent at the same time.
    ///
    /// Counted at the choke points and reported as deltas next to the periodic memory
    /// line:
    ///
    ///   jit 0 / svc 0 / ipc 0 / fifo 0   -- a pure userspace spin: no new code and no
    ///                                       kernel entry at all
    ///   svc N with one id dominating     -- a polling loop; the id names what it polls
    ///   fifo N / frames 0                -- GPU work happens but nothing is presented
    ///
    /// "svc" matters separately from "ipc": most syscalls are not IPC, so a guest
    /// hammering SleepThread or GetSystemTick shows ipc 0 while being anything but idle.
    ///
    /// "threads" counts guest threads entering translated code, not instructions -- it is
    /// a thread-start count, so it stays flat during normal play.
    /// </summary>
    public static class RunCounters
    {
        private static long _guestDispatches;
        private static long _translations;
        private static long _fifoWork;
        private static long _framesPresented;
        private static long _ipcCalls;
        private static long _nopFallbacks;
        private static long _syscalls;

        // Bounded on purpose: a runaway retry loop concentrates on a handful of names,
        // and an unbounded dictionary on the IPC path would be its own problem.
        private const int MaxTrackedServices = 64;
        private static readonly ConcurrentDictionary<string, long> _ipcByService = new();

        // 0x00..0xFF covers the whole SVC space, so this needs no bounding.
        private static readonly long[] _syscallById = new long[256];

        /// <summary>
        /// Set by the emulator side, which is the only layer that can see the guest
        /// kernel. Left null here so this stays free of a dependency on HLE.
        /// </summary>
        public static Func<string[]> ThreadStateProvider { get; set; }

        /// <summary>
        /// True when the last interval saw no GPU work and no presented frames, i.e.
        /// exactly the situation a thread dump is worth printing for.
        /// </summary>
        public static bool LastIntervalIdle { get; private set; }

        public static void GuestDispatch() => Interlocked.Increment(ref _guestDispatches);
        public static void Translation() => Interlocked.Increment(ref _translations);
        public static void FifoWork() => Interlocked.Increment(ref _fifoWork);
        public static void FramePresented() => Interlocked.Increment(ref _framesPresented);

        /// A guest function that failed to compile and was replaced by NOP;RET. The
        /// guest keeps running but that function does nothing, so any non-zero value
        /// here means the emulation is already wrong, whatever the screen shows.
        public static void NopFallback() => Interlocked.Increment(ref _nopFallbacks);

        public static void Syscall(int id)
        {
            Interlocked.Increment(ref _syscalls);

            if ((uint)id < (uint)_syscallById.Length)
            {
                Interlocked.Increment(ref _syscallById[id]);
            }
        }

        public static void IpcCall(string serviceName)
        {
            Interlocked.Increment(ref _ipcCalls);

            if (serviceName == null)
            {
                return;
            }

            if (_ipcByService.Count >= MaxTrackedServices && !_ipcByService.ContainsKey(serviceName))
            {
                return;
            }

            _ipcByService.AddOrUpdate(serviceName, 1, (_, count) => count + 1);
        }

        private static long _lastGuest, _lastTranslations, _lastFifo, _lastFrames, _lastIpc, _lastNops, _lastSyscalls;
        private static readonly long[] _lastSyscallById = new long[256];

        /// <summary>
        /// One line of deltas since the previous call, plus the busiest syscalls and
        /// services. Naming the busiest syscall is what distinguishes a polling loop from
        /// an idle guest, and it names what is being polled.
        /// </summary>
        public static string SampleDelta()
        {
            long guest = Interlocked.Read(ref _guestDispatches);
            long translations = Interlocked.Read(ref _translations);
            long fifo = Interlocked.Read(ref _fifoWork);
            long frames = Interlocked.Read(ref _framesPresented);
            long ipc = Interlocked.Read(ref _ipcCalls);
            long nops = Interlocked.Read(ref _nopFallbacks);
            long syscalls = Interlocked.Read(ref _syscalls);

            LastIntervalIdle = (fifo - _lastFifo) == 0 && (frames - _lastFrames) == 0;

            string line = string.Format(
                "threads {0}  jit {1}  svc {2}  fifo {3}  frames {4}  ipc {5}  nop {6}",
                guest - _lastGuest,
                translations - _lastTranslations,
                syscalls - _lastSyscalls,
                fifo - _lastFifo,
                frames - _lastFrames,
                ipc - _lastIpc,
                nops - _lastNops);

            _lastGuest = guest;
            _lastTranslations = translations;
            _lastFifo = fifo;
            _lastFrames = frames;
            _lastIpc = ipc;
            _lastNops = nops;
            _lastSyscalls = syscalls;

            string syscallTop = TopSyscallDelta();

            if (syscallTop.Length != 0)
            {
                line += "  svc[" + syscallTop + "]";
            }

            if (!_ipcByService.IsEmpty)
            {
                string busiest = string.Join(", ", _ipcByService
                    .ToArray()
                    .OrderByDescending(pair => pair.Value)
                    .Take(3)
                    .Select(pair => $"{pair.Key}={pair.Value}"));

                line += "  ipc[" + busiest + "]";
            }

            return line;
        }

        private static string TopSyscallDelta()
        {
            (int Id, long Count)[] deltas = new (int, long)[_syscallById.Length];

            for (int id = 0; id < _syscallById.Length; id++)
            {
                long total = Interlocked.Read(ref _syscallById[id]);
                deltas[id] = (id, total - _lastSyscallById[id]);
                _lastSyscallById[id] = total;
            }

            return string.Join(", ", deltas
                .Where(entry => entry.Count > 0)
                .OrderByDescending(entry => entry.Count)
                .Take(4)
                .Select(entry => $"{SyscallName(entry.Id)}={entry.Count}"));
        }

        /// <summary>
        /// The syscalls worth recognising on sight when diagnosing a stuck guest. Anything
        /// else is reported by number, which is still enough to look up.
        /// </summary>
        private static string SyscallName(int id) => id switch
        {
            0x01 => "SetHeapSize",
            0x03 => "SetMemoryAttribute",
            0x04 => "MapMemory",
            0x06 => "QueryMemory",
            0x07 => "ExitProcess",
            0x08 => "CreateThread",
            0x09 => "StartThread",
            0x0A => "ExitThread",
            0x0B => "SleepThread",
            0x0C => "GetThreadPriority",
            0x0D => "SetThreadPriority",
            0x0F => "SetThreadCoreMask",
            0x10 => "GetCurrentProcessorNumber",
            0x11 => "SignalEvent",
            0x12 => "ClearEvent",
            0x13 => "MapSharedMemory",
            0x16 => "CloseHandle",
            0x17 => "ResetSignal",
            0x18 => "WaitSynchronization",
            0x19 => "CancelSynchronization",
            0x1A => "ArbitrateLock",
            0x1B => "ArbitrateUnlock",
            0x1C => "WaitProcessWideKeyAtomic",
            0x1D => "SignalProcessWideKey",
            0x1E => "GetSystemTick",
            0x1F => "ConnectToNamedPort",
            0x21 => "SendSyncRequest",
            0x25 => "GetThreadId",
            0x26 => "Break",
            0x27 => "OutputDebugString",
            0x29 => "GetInfo",
            0x2C => "MapPhysicalMemory",
            0x2D => "UnmapPhysicalMemory",
            0x30 => "GetResourceLimitLimitValue",
            0x33 => "GetThreadContext3",
            0x34 => "WaitForAddress",
            0x35 => "SignalToAddress",
            _ => $"svc0x{id:X2}",
        };

        public static string SampleTotals() => string.Format(
            "threads {0}  jit {1}  svc {2}  fifo {3}  frames {4}  ipc {5}  nop {6}",
            Interlocked.Read(ref _guestDispatches),
            Interlocked.Read(ref _translations),
            Interlocked.Read(ref _syscalls),
            Interlocked.Read(ref _fifoWork),
            Interlocked.Read(ref _framesPresented),
            Interlocked.Read(ref _ipcCalls),
            Interlocked.Read(ref _nopFallbacks));
    }
}

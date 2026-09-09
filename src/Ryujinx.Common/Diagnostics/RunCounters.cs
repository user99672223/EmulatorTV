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
    /// spinning inside already-translated code produces no log output at all. So the
    /// emulator can be fully wedged and fully silent at the same time.
    ///
    /// These are counted at four choke points and reported as deltas next to the
    /// periodic memory line, which turns that ambiguity into one readable answer:
    ///
    ///   jit 0 / ipc 0 / fifo 0 / frames 0   -- wedged: no new code, no calls, no GPU work
    ///   jit N / ipc N / fifo 0 / frames 0   -- guest advancing but submitting no GPU work
    ///   jit . / ipc . / fifo N / frames 0   -- GPU work happens but nothing is presented
    ///   jit . / ipc . / fifo N / frames N   -- rendering; a black screen is then the game's own
    ///
    /// "threads" counts guest threads entering translated code, not instructions --
    /// it is a thread-start count, so it stays flat during normal play.
    ///
    /// Everything here is Interlocked and allocation-free on the hot paths; the service
    /// histogram is only touched on IPC dispatch, which is not hot.
    /// </summary>
    public static class RunCounters
    {
        private static long _guestDispatches;
        private static long _translations;
        private static long _fifoWork;
        private static long _framesPresented;
        private static long _ipcCalls;

        // Bounded on purpose: a runaway retry loop concentrates on a handful of names,
        // and an unbounded dictionary on the IPC path would be its own problem.
        private const int MaxTrackedServices = 64;
        private static readonly ConcurrentDictionary<string, long> _ipcByService = new();

        public static void GuestDispatch() => Interlocked.Increment(ref _guestDispatches);
        public static void Translation() => Interlocked.Increment(ref _translations);
        public static void FifoWork() => Interlocked.Increment(ref _fifoWork);
        public static void FramePresented() => Interlocked.Increment(ref _framesPresented);

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

        private static long _lastGuest, _lastTranslations, _lastFifo, _lastFrames, _lastIpc;

        /// <summary>
        /// One line of deltas since the previous call, plus the busiest services. Naming
        /// the busiest service is what distinguishes a retry loop from an idle guest --
        /// a game stuck re-asking nifm shows nifm at thousands of calls per interval.
        /// </summary>
        public static string SampleDelta()
        {
            long guest = Interlocked.Read(ref _guestDispatches);
            long translations = Interlocked.Read(ref _translations);
            long fifo = Interlocked.Read(ref _fifoWork);
            long frames = Interlocked.Read(ref _framesPresented);
            long ipc = Interlocked.Read(ref _ipcCalls);

            string line = string.Format(
                "threads {0}  jit {1}  fifo {2}  frames {3}  ipc {4}",
                guest - _lastGuest,
                translations - _lastTranslations,
                fifo - _lastFifo,
                frames - _lastFrames,
                ipc - _lastIpc);

            _lastGuest = guest;
            _lastTranslations = translations;
            _lastFifo = fifo;
            _lastFrames = frames;
            _lastIpc = ipc;

            if (!_ipcByService.IsEmpty)
            {
                string busiest = string.Join(", ", _ipcByService
                    .ToArray()
                    .OrderByDescending(pair => pair.Value)
                    .Take(3)
                    .Select(pair => $"{pair.Key}={pair.Value}"));

                line += "  [" + busiest + "]";
            }

            return line;
        }

        public static string SampleTotals() => string.Format(
            "threads {0}  jit {1}  fifo {2}  frames {3}  ipc {4}",
            Interlocked.Read(ref _guestDispatches),
            Interlocked.Read(ref _translations),
            Interlocked.Read(ref _fifoWork),
            Interlocked.Read(ref _framesPresented),
            Interlocked.Read(ref _ipcCalls));
    }
}

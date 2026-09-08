using Ryujinx.Common.Logging;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Common.SystemInfo
{
    /// <summary>
    /// Reports how much memory the process has left before the OS kills it, on Apple
    /// mobile platforms (iOS and tvOS).
    ///
    /// On the Apple TV the per-app jetsam limit is the constraint that decides whether a
    /// given guest memory pool size can work at all, so these are the numbers the pool
    /// size should be chosen from. The interesting figure is the drop in available memory
    /// between "launch" and "guest pool reserved": that gap is the emulator's own
    /// overhead, and whatever is left after it is the budget the guest has to fit inside.
    /// </summary>
    public static class AppleMemoryProbe
    {
        private const string SystemLib = "libSystem.B.dylib";

        /// <summary>
        /// Bytes the process can still allocate before it hits its jetsam limit.
        /// iOS 13+ / tvOS 13+. Returns 0 when the process is not memory limited
        /// (and on platforms where it does not exist), so 0 means "no answer",
        /// not "no memory".
        /// </summary>
        [DllImport(SystemLib, EntryPoint = "os_proc_available_memory")]
        private static extern nuint OsProcAvailableMemory();

        private const double Mib = 1024.0 * 1024.0;

        private static long _baselineAvailable = -1;
        private static Timer _sampler;
        private static int _sampleCount;

        /// <summary>
        /// True on the platforms where os_proc_available_memory() exists.
        /// </summary>
        public static bool IsSupported => OperatingSystem.IsIOS() || OperatingSystem.IsTvOS();

        /// <summary>
        /// Bytes remaining before the process hits its memory limit, or -1 if unavailable.
        /// </summary>
        public static long AvailableBytes()
        {
            if (!IsSupported)
            {
                return -1;
            }

            try
            {
                nuint available = OsProcAvailableMemory();

                // The call reports 0 for a process with no limit applied, which is not
                // something we can distinguish from a genuine answer of zero. Treat it
                // as "unavailable" rather than reporting a scary 0 MiB.
                return available == 0 ? -1 : (long)available;
            }
            catch (EntryPointNotFoundException)
            {
                return -1;
            }
            catch (DllNotFoundException)
            {
                return -1;
            }
        }

        /// <summary>
        /// Logs a labelled memory sample. The first call establishes the baseline that
        /// later samples are compared against.
        /// </summary>
        public static void Log(string stage)
        {
            long available = AvailableBytes();
            long workingSet = SafeWorkingSet();
            long managed = GC.GetTotalMemory(false);

            if (_baselineAvailable < 0 && available >= 0)
            {
                _baselineAvailable = available;
            }

            string availableText = available >= 0
                ? $"{available / Mib,8:F1} MiB"
                : "     n/a";

            string usedText = workingSet > 0
                ? $"{workingSet / Mib,8:F1} MiB"
                : "     n/a";

            string deltaText = string.Empty;

            if (available >= 0 && _baselineAvailable >= 0)
            {
                double consumed = (_baselineAvailable - available) / Mib;
                deltaText = $"   consumed since launch {consumed,8:F1} MiB";
            }

            Logger.Notice.Print(LogClass.Application,
                $"[MEM] {stage,-32} available {availableText}   resident {usedText}   managed {managed / Mib,7:F1} MiB{deltaText}");
        }

        private static long SafeWorkingSet()
        {
            try
            {
                return Environment.WorkingSet;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Starts sampling in the background, for the "steady state, in game" reading.
        /// A single sample taken at an arbitrary moment is misleading because memory
        /// climbs while assets and shaders stream in, so this samples repeatedly and
        /// each line carries its elapsed time.
        /// </summary>
        public static void StartPeriodicLogging(TimeSpan interval, string label = "in game")
        {
            StopPeriodicLogging();

            _sampleCount = 0;

            long intervalMs = (long)interval.TotalMilliseconds;

            _sampler = new Timer(_ =>
            {
                int n = Interlocked.Increment(ref _sampleCount);
                long seconds = (long)interval.TotalSeconds * n;

                try
                {
                    Log($"{label} t+{seconds}s");
                }
                catch
                {
                    // Never let a diagnostic take the emulator down.
                }
            }, null, intervalMs, intervalMs);
        }

        public static void StopPeriodicLogging()
        {
            Interlocked.Exchange(ref _sampler, null)?.Dispose();
        }
    }
}

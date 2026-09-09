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
        // CLOCK_PROCESS_CPUTIME_ID on Darwin.
        private const int ClockProcessCpuTimeId = 12;

        [StructLayout(LayoutKind.Sequential)]
        private struct TimeSpec
        {
            public long Seconds;
            public long Nanoseconds;
        }

        [DllImport(SystemLib, EntryPoint = "clock_gettime", SetLastError = true)]
        private static extern int ClockGetTime(int clockId, out TimeSpec ts);

        private static double _lastCpuSeconds = -1;

        /// <summary>Process CPU seconds consumed, or -1 when unavailable.</summary>
        private static double CpuSeconds()
        {
            try
            {
                if (ClockGetTime(ClockProcessCpuTimeId, out TimeSpec ts) != 0)
                {
                    return -1;
                }

                return ts.Seconds + (ts.Nanoseconds / 1_000_000_000.0);
            }
            catch
            {
                return -1;
            }
        }

        [DllImport(SystemLib, EntryPoint = "os_proc_available_memory")]
        private static extern nuint OsProcAvailableMemory();

        // task_info(TASK_VM_INFO) exposes phys_footprint, which is the figure jetsam
        // actually meters against the per-process limit -- closer to the truth than
        // resident_size, which excludes compressed pages and some IOKit mappings.
        [DllImport(SystemLib, EntryPoint = "task_info")]
        private static extern int TaskInfo(uint task, uint flavor, IntPtr info, ref uint count);

        [DllImport(SystemLib, EntryPoint = "task_self_trap")]
        private static extern uint TaskSelfTrap();

        private const uint TaskVmInfo = 22;

        // Byte offset of phys_footprint within task_vm_info_data_t: 19 fields precede
        // it, of which region_count and page_size are 4 bytes and the rest 8.
        private const int PhysFootprintOffset = 144;

        // Buffer generously; the struct has grown across SDK revisions and task_info
        // writes back the count it actually filled.
        private const int TaskVmInfoBufferBytes = 512;

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

                if (workingSet > 0)
                {
                    Logger.Notice.Print(LogClass.Application,
                        $"[MEM] process memory limit looks like {(available + workingSet) / Mib:F0} MiB " +
                        $"(available {available / Mib:F1} + footprint {workingSet / Mib:F1})");
                }
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

        /// <summary>
        /// Physical footprint in bytes as metered by jetsam, or -1 if unavailable.
        /// Added to <see cref="AvailableBytes"/> this recovers the process's actual
        /// memory limit, which Apple does not publish for any tvOS device.
        /// </summary>
        public static long PhysFootprintBytes()
        {
            if (!IsSupported)
            {
                return -1;
            }

            IntPtr buffer = Marshal.AllocHGlobal(TaskVmInfoBufferBytes);

            try
            {
                for (int i = 0; i < TaskVmInfoBufferBytes; i += 8)
                {
                    Marshal.WriteInt64(buffer, i, 0);
                }

                uint count = TaskVmInfoBufferBytes / sizeof(uint);

                if (TaskInfo(TaskSelfTrap(), TaskVmInfo, buffer, ref count) != 0)
                {
                    return -1;
                }

                // Older kernels return a struct that stops short of phys_footprint.
                if (count * sizeof(uint) < PhysFootprintOffset + sizeof(long))
                {
                    return -1;
                }

                long footprint = Marshal.ReadInt64(buffer, PhysFootprintOffset);

                // Sanity check rather than trust the offset blindly: any real answer is
                // well above a megabyte and well under the 4 GiB this hardware has.
                if (footprint < 1 * 1024 * 1024 || footprint > 8L * 1024 * 1024 * 1024)
                {
                    return -1;
                }

                return footprint;
            }
            catch
            {
                return -1;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static long SafeWorkingSet()
        {
            long footprint = PhysFootprintBytes();

            if (footprint > 0)
            {
                return footprint;
            }

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

                    // Deltas, so a static line means genuinely nothing happened in
                    // the interval rather than nothing having happened since boot.
                    double cpuNow = CpuSeconds();
                    string cpuText;

                    if (cpuNow < 0)
                    {
                        cpuText = "cpu unavailable";
                    }
                    else if (_lastCpuSeconds < 0)
                    {
                        cpuText = $"cpu {cpuNow:0.00}s total";
                    }
                    else
                    {
                        double used = cpuNow - _lastCpuSeconds;
                        double wall = interval.TotalSeconds;

                        // Around 100% of one core means a spin; near 0% means blocked.
                        cpuText = $"cpu {used:0.00}s of {wall:0}s wall ({(used / wall) * 100.0:0}% of one core)";
                    }

                    _lastCpuSeconds = cpuNow;

                    Logger.Notice.Print(LogClass.Application,
                        $"[RUN] t+{seconds}s  {Diagnostics.RunCounters.SampleDelta()}  {cpuText}");

                    // Only while nothing is reaching the screen. During normal play this
                    // stays silent instead of printing a wall of threads every interval.
                    if (Diagnostics.RunCounters.LastIntervalIdle)
                    {
                        string[] dump = Diagnostics.RunCounters.ThreadStateProvider?.Invoke();

                        if (dump != null)
                        {
                            foreach (string entry in dump)
                            {
                                Logger.Notice.Print(LogClass.Application, entry);
                            }
                        }
                    }
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

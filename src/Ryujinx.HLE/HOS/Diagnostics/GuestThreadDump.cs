using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Kernel.Threading;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Ryujinx.HLE.HOS.Diagnostics
{
    /// <summary>
    /// Names what every guest thread is parked on.
    ///
    /// A frozen guest with no log output, no GPU work and no service calls gives no
    /// clue on its own about which of its threads is stuck or what it is stuck on. The
    /// scheduler state plus the object a thread is waiting for turns "it hangs" into a
    /// specific kernel object that never signals.
    ///
    /// This only reads state, never takes the scheduler lock, and never blocks. It runs
    /// on a diagnostic timer while the emulator is presumed wedged, so it must not be
    /// capable of making the situation worse.
    /// </summary>
    static class GuestThreadDump
    {
        internal static string[] Collect(KernelContext context)
        {
            if (context == null)
            {
                return new[] { "[THREADS] no kernel context" };
            }

            List<string> lines = new();

            try
            {
                KProcess[] processes = context.Processes.Values.ToArray();

                foreach (KProcess process in processes)
                {
                    KThread[] threads;

                    try
                    {
                        threads = process.GetThreadsSnapshot();
                    }
                    catch (Exception ex)
                    {
                        lines.Add($"[THREADS] pid {process.Pid}: snapshot failed: {ex.GetType().Name}");
                        continue;
                    }

                    lines.Add($"[THREADS] pid {process.Pid} \"{process.Name}\": {threads.Length} threads");

                    foreach (KThread thread in threads)
                    {
                        lines.Add("[THREADS]   " + Describe(thread));
                    }
                }
            }
            catch (Exception ex)
            {
                lines.Add($"[THREADS] dump failed: {ex.GetType().Name}: {ex.Message}");
            }

            if (lines.Count == 0)
            {
                lines.Add("[THREADS] no processes");
            }

            try
            {
                lines.AddRange(SampleRunningThreads(context));
            }
            catch (Exception ex)
            {
                lines.Add($"[PCSAMPLE] failed: {ex.GetType().Name}: {ex.Message}");
            }

            return lines.ToArray();
        }

        private const int SampleCount = 40;
        private const int SampleIntervalMs = 50;

        /// <summary>
        /// Samples the program counter of every Running guest thread, repeatedly, and
        /// reports the distinct addresses with hit counts.
        ///
        /// A thread that is Running rather than blocked is executing something, and if
        /// nothing else in the emulator moves then it is a loop. A tight cluster of
        /// addresses names that loop; a single repeated address names it exactly.
        ///
        /// Caveat worth stating in the output rather than hiding: IExecutionContext.Pc
        /// is documented as not necessarily pointing at the last instruction executed.
        /// It is refreshed when guest code returns to the dispatcher, so a loop that
        /// stays entirely inside one translated block can report a stale, constant Pc.
        /// "Pc never changed" therefore means either a very tight loop or a Pc that is
        /// simply not being updated -- which is why the registers are dumped too, since
        /// those moving while the Pc does not still proves the guest is live.
        /// </summary>
        private static string[] SampleRunningThreads(KernelContext context)
        {
            List<KThread> running = new();

            foreach (KProcess process in context.Processes.Values.ToArray())
            {
                foreach (KThread thread in process.GetThreadsSnapshot())
                {
                    if ((thread.SchedFlags & ThreadSchedState.LowMask) == ThreadSchedState.Running &&
                        thread.Context != null)
                    {
                        running.Add(thread);
                    }
                }
            }

            if (running.Count == 0)
            {
                return new[] { "[PCSAMPLE] no threads in Running state" };
            }

            Dictionary<KThread, Dictionary<ulong, int>> histograms = new();

            foreach (KThread thread in running)
            {
                histograms[thread] = new Dictionary<ulong, int>();
            }

            for (int i = 0; i < SampleCount; i++)
            {
                foreach (KThread thread in running)
                {
                    ulong pc;

                    try
                    {
                        pc = thread.Context.Pc;
                    }
                    catch
                    {
                        continue;
                    }

                    histograms[thread].TryGetValue(pc, out int hits);
                    histograms[thread][pc] = hits + 1;
                }

                Thread.Sleep(SampleIntervalMs);
            }

            List<string> lines = new();

            foreach (KThread thread in running)
            {
                Dictionary<ulong, int> histogram = histograms[thread];

                lines.Add($"[PCSAMPLE] uid {thread.ThreadUid} {Name(thread)}: " +
                          $"{SampleCount} samples over {SampleCount * SampleIntervalMs}ms, " +
                          $"{histogram.Count} distinct pc");

                foreach (KeyValuePair<ulong, int> entry in histogram
                    .OrderByDescending(e => e.Value)
                    .Take(12))
                {
                    lines.Add($"[PCSAMPLE]   0x{entry.Key:X16}  x{entry.Value}");
                }

                if (histogram.Count == 1)
                {
                    lines.Add("[PCSAMPLE]   (single address: either a very tight loop, or " +
                              "Pc is not refreshed while inside one translated block -- " +
                              "compare the register dumps below)");
                }

                lines.AddRange(DumpRegisters(thread));
            }

            return lines.ToArray();
        }

        /// <summary>
        /// Two register snapshots a moment apart. Registers that differ prove the thread
        /// is genuinely executing even when the Pc looks frozen, and the values often
        /// identify what is being polled.
        /// </summary>
        private static string[] DumpRegisters(KThread thread)
        {
            List<string> lines = new();

            for (int pass = 0; pass < 2; pass++)
            {
                try
                {
                    List<string> parts = new();

                    for (int reg = 0; reg <= 30; reg++)
                    {
                        parts.Add($"x{reg}=0x{thread.Context.GetX(reg):X}");
                    }

                    lines.Add($"[PCREGS] uid {thread.ThreadUid} pass{pass} " +
                              $"pc=0x{thread.Context.Pc:X16} " + string.Join(" ", parts));
                }
                catch (Exception ex)
                {
                    lines.Add($"[PCREGS] uid {thread.ThreadUid} pass{pass} failed: {ex.GetType().Name}");
                }

                if (pass == 0)
                {
                    Thread.Sleep(200);
                }
            }

            return lines.ToArray();
        }

        private static string Describe(KThread thread)
        {
            try
            {
                // LowMask is the run state proper; the rest of SchedFlags carries the
                // pause reasons, which are worth seeing separately because a thread
                // paused for debug reads very differently from one waiting on a mutex.
                ThreadSchedState state = thread.SchedFlags & ThreadSchedState.LowMask;
                ThreadSchedState pauseFlags = thread.SchedFlags & ~ThreadSchedState.LowMask;

                string text = $"uid {thread.ThreadUid,-4} {Name(thread),-24} {state}";

                if (pauseFlags != 0)
                {
                    text += $" +{pauseFlags}";
                }

                if (thread.WaitingSync)
                {
                    int count = thread.WaitSyncObjects?.Length ?? 0;
                    text += $"  waitsync[{count}]";

                    if (count > 0)
                    {
                        // The type of the object is what identifies the thing that never
                        // signals: a KEvent, a KServerSession waiting on a reply, and so on.
                        IEnumerable<string> kinds = thread.WaitSyncObjects
                            .Take(4)
                            .Select(o => o?.GetType().Name ?? "null");

                        text += "=" + string.Join(",", kinds);
                    }

                    if (thread.ObjSyncResult.ErrorCode != 0)
                    {
                        text += $" result=0x{thread.ObjSyncResult.ErrorCode:X}";
                    }
                }

                if (thread.MutexAddress != 0)
                {
                    text += $"  mutex=0x{thread.MutexAddress:X}";

                    if (thread.MutexOwner != null)
                    {
                        text += $" ownedBy=uid {thread.MutexOwner.ThreadUid}";
                    }
                    else
                    {
                        text += " ownedBy=none";
                    }
                }

                if (thread.CondVarAddress != 0)
                {
                    text += $"  condvar=0x{thread.CondVarAddress:X}";
                }

                if (thread.SignaledObj != null)
                {
                    text += $"  signaledBy={thread.SignaledObj.GetType().Name}";
                }

                return text;
            }
            catch (Exception ex)
            {
                return $"uid ?  describe failed: {ex.GetType().Name}";
            }
        }

        private static string Name(KThread thread)
        {
            try
            {
                return thread.HostThread?.Name ?? "?";
            }
            catch
            {
                return "?";
            }
        }
    }
}

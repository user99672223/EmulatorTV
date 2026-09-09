using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Kernel.Threading;
using System;
using System.Collections.Generic;
using System.Linq;

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

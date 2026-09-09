using Ryujinx.Common.Logging;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Memory
{
    /// <summary>
    /// Establishes, by experiment, which kinds of executable memory this device grants.
    ///
    /// Three mechanisms have now been tried through the emulator and each failed
    /// differently: plain anonymous mmap yields max=rw- so nothing can add execute
    /// later, the mach memory-entry route reports success while clamping execute away,
    /// and MAP_JIT is refused outright with EPERM even though CS_DEBUGGED is set. Those
    /// were measured one build at a time. This measures all of them in a single run,
    /// before the emulator commits to a strategy, so the question stops being answered
    /// by inference.
    ///
    /// The last test is the one that matters: write a single RET instruction into the
    /// region and call it. Anything short of that only shows what the kernel claims
    /// about the mapping, not whether the CPU will fetch from it.
    /// </summary>
    public static class JitCapabilityProbe
    {
        private const string Lib = "libc";

        private const int PROT_NONE = 0;
        private const int PROT_READ = 1;
        private const int PROT_WRITE = 2;
        private const int PROT_EXEC = 4;

        private const int MAP_PRIVATE = 0x0002;
        private const int MAP_ANON = 0x1000;
        private const int MAP_JIT = 0x0800;
        private const int MAP_NORESERVE = 0x0040;

        private const int BasicInfo64 = 9;
        private const uint BasicInfo64Count = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct VmRegionBasicInfo64
        {
            public int Protection;
            public int MaxProtection;
            public uint Inheritance;
            public int Shared;
            public int Reserved;
            public ulong Offset;
            public int Behavior;
            public ushort UserWiredCount;
        }

        [DllImport(Lib, EntryPoint = "mmap", SetLastError = true)]
        private static extern IntPtr Mmap(IntPtr addr, nuint length, int prot, int flags, int fd, long offset);

        [DllImport(Lib, EntryPoint = "munmap", SetLastError = true)]
        private static extern int Munmap(IntPtr addr, nuint length);

        [DllImport(Lib, EntryPoint = "mprotect", SetLastError = true)]
        private static extern int Mprotect(IntPtr addr, nuint length, int prot);

        [DllImport(Lib, EntryPoint = "sys_icache_invalidate")]
        private static extern void SysIcacheInvalidate(IntPtr start, nuint len);

        [DllImport(Lib, EntryPoint = "task_self_trap")]
        private static extern uint TaskSelfTrap();

        [DllImport(Lib, EntryPoint = "vm_region_64")]
        private static extern int VmRegion64(uint task, ref ulong address, ref ulong size, int flavor,
            ref VmRegionBasicInfo64 info, ref uint count, out uint objectName);

        [DllImport(Lib, EntryPoint = "pthread_jit_write_protect_np")]
        private static extern void PthreadJitWriteProtect(int enabled);

        [DllImport(Lib, EntryPoint = "mach_task_self")]
        private static extern uint MachTaskSelf();

        [DllImport(Lib, EntryPoint = "vm_remap")]
        private static extern int VmRemap(uint targetTask, ref ulong targetAddress, ulong size, ulong mask,
            int anywhere, uint srcTask, ulong srcAddress, int copy,
            ref uint curProtection, ref uint maxProtection, int inheritance);

        [DllImport(Lib, EntryPoint = "vm_protect")]
        private static extern int VmProtect(uint task, ulong address, ulong size, int setMaximum, int newProtection);

        private static string Prot(int p) => string.Concat(
            (p & PROT_READ) != 0 ? "r" : "-",
            (p & PROT_WRITE) != 0 ? "w" : "-",
            (p & PROT_EXEC) != 0 ? "x" : "-");

        private static string Actual(IntPtr ptr)
        {
            try
            {
                ulong address = (ulong)ptr;
                ulong size = 0;
                VmRegionBasicInfo64 info = default;
                uint count = BasicInfo64Count;

                if (VmRegion64(TaskSelfTrap(), ref address, ref size, BasicInfo64, ref info, ref count, out _) != 0)
                {
                    return "region query failed";
                }

                return $"cur={Prot(info.Protection)} max={Prot(info.MaxProtection)}";
            }
            catch
            {
                return "region query threw";
            }
        }

        private const nuint TestSize = 16384;

        private static void Case(string name, int prot, int extraFlags, nuint size = TestSize)
        {
            IntPtr ptr = Mmap(IntPtr.Zero, size, prot, MAP_ANON | MAP_PRIVATE | extraFlags, -1, 0);

            if (ptr == new IntPtr(-1))
            {
                Logger.Notice.Print(LogClass.Cpu,
                    $"[JITCAP] {name,-34} mmap FAILED errno={Marshal.GetLastPInvokeError()}");

                return;
            }

            Logger.Notice.Print(LogClass.Cpu, $"[JITCAP] {name,-34} ok at 0x{ptr:X} -> {Actual(ptr)}");

            Munmap(ptr, size);
        }

        /// <summary>
        /// The only strategy the earlier results leave open, and the one NoWxCache already
        /// implements: map read/write, write the code, flip the page to read/execute, call
        /// it. W^X is enforced here -- asking mmap for rwx yields rw- -- but every small
        /// mapping reports max=rwx, so the flip should be permitted.
        /// </summary>
        private static void WriteThenExecute(string name, nuint size, int extraFlags)
        {
            IntPtr ptr = Mmap(IntPtr.Zero, size, PROT_READ | PROT_WRITE,
                MAP_ANON | MAP_PRIVATE | extraFlags, -1, 0);

            if (ptr == new IntPtr(-1))
            {
                Logger.Notice.Print(LogClass.Cpu,
                    $"[JITCAP] {name,-34} mmap FAILED errno={Marshal.GetLastPInvokeError()}");

                return;
            }

            try
            {
                Logger.Notice.Print(LogClass.Cpu, $"[JITCAP] {name,-34} mapped rw -> {Actual(ptr)}");

                Marshal.WriteInt32(ptr, unchecked((int)0xD65F03C0));    // AArch64 RET

                if (Mprotect(ptr, size, PROT_READ | PROT_EXEC) != 0)
                {
                    Logger.Notice.Print(LogClass.Cpu,
                        $"[JITCAP] {name,-34} mprotect r-x FAILED errno={Marshal.GetLastPInvokeError()} ({Actual(ptr)})");

                    return;
                }

                string after = Actual(ptr);

                if (!after.Contains("cur=r-x"))
                {
                    Logger.Notice.Print(LogClass.Cpu,
                        $"[JITCAP] {name,-34} mprotect returned ok but page is {after}; NOT calling it");

                    return;
                }

                SysIcacheInvalidate(ptr, size);

                Logger.Notice.Print(LogClass.Cpu, $"[JITCAP] {name,-34} now {after}, calling it");

                Marshal.GetDelegateForFunctionPointer<Action>(ptr)();

                Logger.Notice.Print(LogClass.Cpu,
                    $"[JITCAP] {name,-34} EXECUTED SUCCESSFULLY -- usable for JIT");
            }
            catch (Exception ex)
            {
                Logger.Notice.Print(LogClass.Cpu, $"[JITCAP] {name,-34} threw: {ex.GetType().Name}");
            }
            finally
            {
                Munmap(ptr, size);
            }
        }

        /// <summary>
        /// The dual mapping, tested end to end.
        ///
        /// This device permits execute only on pages that were created executable and
        /// never writable: mmap with PROT_READ|PROT_EXEC yields cur=r-x, while mprotect
        /// adding execute to a writable page silently fails AND permanently strips execute
        /// from the maximum protection. So the code cannot be written through the mapping
        /// it is executed from. Two mappings of the same physical pages solve that -- one
        /// born r-x for execution, one aliased rw- for writing -- which is what
        /// DualMappedJitAllocator already builds.
        ///
        /// Writes a RET through the writable alias and calls it through the executable one.
        /// </summary>
        private static void DualMapped(string name, nuint size)
        {
            IntPtr rx = Mmap(IntPtr.Zero, size, PROT_READ | PROT_EXEC, MAP_ANON | MAP_PRIVATE, -1, 0);

            if (rx == new IntPtr(-1))
            {
                Logger.Notice.Print(LogClass.Cpu,
                    $"[JITCAP] {name,-34} rx mmap FAILED errno={Marshal.GetLastPInvokeError()}");

                return;
            }

            Logger.Notice.Print(LogClass.Cpu, $"[JITCAP] {name,-34} rx at 0x{rx:X} -> {Actual(rx)}");

            ulong rw = 0;
            uint cur = 0, max = 0;

            int remap = VmRemap(MachTaskSelf(), ref rw, size, 0, 1, MachTaskSelf(), (ulong)rx, 0,
                ref cur, ref max, 2);

            if (remap != 0)
            {
                Logger.Notice.Print(LogClass.Cpu, $"[JITCAP] {name,-34} vm_remap FAILED {remap}");
                Munmap(rx, size);

                return;
            }

            int prot = VmProtect(MachTaskSelf(), rw, size, 0, PROT_READ | PROT_WRITE);

            Logger.Notice.Print(LogClass.Cpu,
                $"[JITCAP] {name,-34} rw alias at 0x{rw:X} vm_protect={prot} -> {Actual((IntPtr)rw)}");

            if (prot != 0)
            {
                Munmap(rx, size);

                return;
            }

            try
            {
                Marshal.WriteInt32((IntPtr)rw, unchecked((int)0xD65F03C0));    // AArch64 RET

                SysIcacheInvalidate(rx, size);

                string view = Actual(rx);

                if (!view.Contains("cur=r-x"))
                {
                    Logger.Notice.Print(LogClass.Cpu,
                        $"[JITCAP] {name,-34} rx view is {view}; NOT calling it");

                    return;
                }

                Logger.Notice.Print(LogClass.Cpu, $"[JITCAP] {name,-34} rx view {view}, calling it");

                Marshal.GetDelegateForFunctionPointer<Action>(rx)();

                Logger.Notice.Print(LogClass.Cpu,
                    $"[JITCAP] {name,-34} EXECUTED SUCCESSFULLY -- dual mapping works, use this");
            }
            catch (Exception ex)
            {
                Logger.Notice.Print(LogClass.Cpu, $"[JITCAP] {name,-34} threw: {ex.GetType().Name}");
            }
            finally
            {
                Munmap(rx, size);
            }
        }

        private static int _ran;

        /// <summary>
        /// Runs once, at game start, which is after the debugger attach has set
        /// CS_DEBUGGED. Running it at app launch instead would measure the wrong state.
        /// </summary>
        public static void Run()
        {
            if (System.Threading.Interlocked.Exchange(ref _ran, 1) != 0)
            {
                return;
            }

            try
            {
                Logger.Notice.Print(LogClass.Cpu, "[JITCAP] probing what this device grants for executable memory");

                // The only remaining strategy, first: write through an aliased rw mapping
                // and execute through one that was born r-x.
                DualMapped("dual: 16K rx + rw alias", 16384);
                DualMapped("dual: 256M rx + rw alias", (nuint)(256 * 1024 * 1024));

                WriteThenExecute("exec: 16K rw->rx", 16384, 0);
                WriteThenExecute("exec: 16K rw->rx +NORESERVE", 16384, MAP_NORESERVE);

                // Why the real JIT region measured max=rw- while these small maps get
                // max=rwx: the reservation is 2 GiB and carries MAP_NORESERVE.
                Case("prop: 16K none", PROT_NONE, 0);
                Case("prop: 16K none +NORESERVE", PROT_NONE, MAP_NORESERVE);
                Case("prop: 256M none", PROT_NONE, 0, (nuint)(256 * 1024 * 1024));
                Case("prop: 256M none +NORESERVE", PROT_NONE, MAP_NORESERVE, (nuint)(256 * 1024 * 1024));
                Case("prop: 2G none", PROT_NONE, 0, (nuint)0x7FF00000);
                Case("prop: 2G none +NORESERVE", PROT_NONE, MAP_NORESERVE, (nuint)0x7FF00000);

                // And whether the flip still works inside a large reservation.
                WriteThenExecute("exec: 256M rw->rx", (nuint)(256 * 1024 * 1024), 0);

                Logger.Notice.Print(LogClass.Cpu, "[JITCAP] probe complete");
            }
            catch (Exception ex)
            {
                Logger.Notice.Print(LogClass.Cpu, $"[JITCAP] probe failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}

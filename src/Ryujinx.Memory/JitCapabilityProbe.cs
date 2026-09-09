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

        private static void Case(string name, int prot, int extraFlags)
        {
            IntPtr ptr = Mmap(IntPtr.Zero, TestSize, prot, MAP_ANON | MAP_PRIVATE | extraFlags, -1, 0);

            if (ptr == new IntPtr(-1))
            {
                Logger.Notice.Print(LogClass.Cpu,
                    $"[JITCAP] {name,-26} mmap FAILED errno={Marshal.GetLastPInvokeError()}");

                return;
            }

            Logger.Notice.Print(LogClass.Cpu, $"[JITCAP] {name,-26} mmap ok at 0x{ptr:X} -> {Actual(ptr)}");

            Munmap(ptr, TestSize);
        }

        /// <summary>
        /// Allocates, writes a RET, and calls it. This is the only test that proves the
        /// CPU will actually fetch instructions from the region.
        /// </summary>
        private static void ExecuteCase(string name, int prot, int extraFlags, bool viaMprotect)
        {
            IntPtr ptr = Mmap(IntPtr.Zero, TestSize, prot, MAP_ANON | MAP_PRIVATE | extraFlags, -1, 0);

            if (ptr == new IntPtr(-1))
            {
                Logger.Notice.Print(LogClass.Cpu,
                    $"[JITCAP] {name,-26} mmap FAILED errno={Marshal.GetLastPInvokeError()}");

                return;
            }

            try
            {
                if (extraFlags == MAP_JIT)
                {
                    try
                    {
                        PthreadJitWriteProtect(0);
                    }
                    catch
                    {
                        // Absent on this platform; not fatal for the experiment.
                    }
                }

                // AArch64 RET.
                Marshal.WriteInt32(ptr, unchecked((int)0xD65F03C0));

                if (viaMprotect)
                {
                    int result = Mprotect(ptr, TestSize, PROT_READ | PROT_EXEC);

                    if (result != 0)
                    {
                        Logger.Notice.Print(LogClass.Cpu,
                            $"[JITCAP] {name,-26} mprotect to r-x FAILED errno={Marshal.GetLastPInvokeError()} ({Actual(ptr)})");

                        return;
                    }
                }

                if (extraFlags == MAP_JIT)
                {
                    try
                    {
                        PthreadJitWriteProtect(1);
                    }
                    catch
                    {
                    }
                }

                SysIcacheInvalidate(ptr, TestSize);

                Logger.Notice.Print(LogClass.Cpu,
                    $"[JITCAP] {name,-26} about to CALL it ({Actual(ptr)})");

                Action call = Marshal.GetDelegateForFunctionPointer<Action>(ptr);
                call();

                Logger.Notice.Print(LogClass.Cpu,
                    $"[JITCAP] {name,-26} EXECUTED SUCCESSFULLY -- this region is usable for JIT");
            }
            catch (Exception ex)
            {
                Logger.Notice.Print(LogClass.Cpu, $"[JITCAP] {name,-26} threw: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Munmap(ptr, TestSize);
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

                Case("mmap rw", PROT_READ | PROT_WRITE, 0);
                Case("mmap rwx", PROT_READ | PROT_WRITE | PROT_EXEC, 0);
                Case("mmap rx", PROT_READ | PROT_EXEC, 0);
                Case("mmap none", PROT_NONE, 0);
                Case("mmap rw + MAP_JIT", PROT_READ | PROT_WRITE, MAP_JIT);
                Case("mmap rwx + MAP_JIT", PROT_READ | PROT_WRITE | PROT_EXEC, MAP_JIT);

                ExecuteCase("exec: rwx direct", PROT_READ | PROT_WRITE | PROT_EXEC, 0, viaMprotect: false);
                ExecuteCase("exec: rw then mprotect rx", PROT_READ | PROT_WRITE, 0, viaMprotect: true);
                ExecuteCase("exec: MAP_JIT rwx", PROT_READ | PROT_WRITE | PROT_EXEC, MAP_JIT, viaMprotect: false);

                Logger.Notice.Print(LogClass.Cpu, "[JITCAP] probe complete");
            }
            catch (Exception ex)
            {
                Logger.Notice.Print(LogClass.Cpu, $"[JITCAP] probe failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}

using Ryujinx.Common.Logging;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Cpu.LightningJit.Cache
{
    /// <summary>
    /// Asks the kernel what protection a mapped page actually carries.
    ///
    /// The emitted dispatch code decodes correctly in every respect -- branch offsets,
    /// table base, fallback call -- yet the process burns a full core without a single
    /// managed frame running and without any evidence that a stub instruction retired.
    /// An instruction fetch from a page that is not executable would do exactly that:
    /// the fault is taken, the memory manager's own SIGSEGV handler treats it as a guest
    /// access and returns, the instruction re-executes, and it faults again forever. No
    /// signal reaches the crash reporter because that handler consumes it.
    ///
    /// Whether the RX half of the dual mapping is genuinely R-X is therefore worth
    /// asking the kernel rather than assuming from the fact that vm_remap returned
    /// success.
    /// </summary>
    static class VmProtectionProbe
    {
        private const string SystemLib = "libc";

        // VM_REGION_BASIC_INFO_64
        private const int BasicInfo64 = 9;

        // Count is in units of natural_t (4 bytes).
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

        [DllImport(SystemLib, EntryPoint = "task_self_trap")]
        private static extern uint TaskSelfTrap();

        [DllImport(SystemLib, EntryPoint = "vm_region_64")]
        private static extern int VmRegion64(
            uint targetTask,
            ref ulong address,
            ref ulong size,
            int flavor,
            ref VmRegionBasicInfo64 info,
            ref uint infoCount,
            out uint objectName);

        private static string Describe(int protection)
        {
            // VM_PROT_READ 1, VM_PROT_WRITE 2, VM_PROT_EXECUTE 4.
            return string.Concat(
                (protection & 1) != 0 ? "r" : "-",
                (protection & 2) != 0 ? "w" : "-",
                (protection & 4) != 0 ? "x" : "-");
        }

        /// <summary>
        /// Logs the current and maximum protection of the region containing the address.
        /// Never throws: this runs while diagnosing a wedged emulator.
        /// </summary>
        public static void Report(string what, IntPtr pointer)
        {
            try
            {
                ulong address = (ulong)pointer;
                ulong size = 0;
                VmRegionBasicInfo64 info = default;
                uint count = BasicInfo64Count;

                int result = VmRegion64(TaskSelfTrap(), ref address, ref size, BasicInfo64,
                    ref info, ref count, out _);

                if (result != 0)
                {
                    Logger.Notice.Print(LogClass.Cpu,
                        $"[VMPROT] {what} 0x{pointer:X}: vm_region_64 failed with {result}.");

                    return;
                }

                Logger.Notice.Print(LogClass.Cpu,
                    $"[VMPROT] {what} 0x{pointer:X}: region 0x{address:X} size 0x{size:X} " +
                    $"cur={Describe(info.Protection)} max={Describe(info.MaxProtection)} " +
                    $"shared={info.Shared != 0}");
            }
            catch (Exception ex)
            {
                Logger.Notice.Print(LogClass.Cpu,
                    $"[VMPROT] {what} 0x{pointer:X}: probe failed: {ex.GetType().Name}");
            }
        }
    }
}

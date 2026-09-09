using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Ryujinx.Memory.MemoryManagerUnixHelper;

namespace Ryujinx.Memory
{
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("ios")]
    [SupportedOSPlatform("tvos")]
    static class MemoryManagementUnix
    {
        private static readonly ConcurrentDictionary<IntPtr, ulong> _allocations = new();

        public static IntPtr Allocate(ulong size, bool forJit)
        {
            return AllocateInternal(size, MmapProts.PROT_READ | MmapProts.PROT_WRITE, forJit);
        }

        public static IntPtr Reserve(ulong size, bool forJit)
        {
            return AllocateInternal(size, MmapProts.PROT_NONE, forJit);
        }

        private static IntPtr AllocateInternal(ulong size, MmapProts prot, bool forJit, bool shared = false)
        {
            MmapFlags flags = MmapFlags.MAP_ANONYMOUS;

            if (shared)
            {
                flags |= MmapFlags.MAP_SHARED | MmapFlags.MAP_UNLOCKED;
            }
            else
            {
                flags |= MmapFlags.MAP_PRIVATE;
            }

            if (prot == MmapProts.PROT_NONE)
            {
                flags |= MmapFlags.MAP_NORESERVE;
            }

            // MAP_JIT is the mechanism that gets executable memory on Apple platforms, and
            // it was never being requested here: this gate said macOS only, while
            // MemoryManagerUnixHelper.ToMmapFlags already passes the flag through for iOS
            // and tvOS explicitly. The plumbing was written for these platforms and only
            // the caller excluded them -- the same macOS-only-gate mistake that appears
            // throughout this tree.
            //
            // It matters because plain anonymous mmap here yields max=rw-, measured: no
            // execute bit in the MAXIMUM protection, so no later mprotect or vm_protect can
            // introduce one. MAP_JIT is what makes the kernel grant it, and the entitlement
            // it would normally require is waived while CS_DEBUGGED is set, which it is by
            // the time a game starts.
            // MAP_JIT is NOT requested on iOS/tvOS: mmap refuses it with EPERM here even
            // with CS_DEBUGGED set, which aborts the boot before anything else runs. Kept
            // to macOS, where it is what the flag was written for. JitCapabilityProbe
            // measures this directly rather than leaving it as a claim.
            if (OperatingSystem.IsMacOS() && OperatingSystem.IsMacOSVersionAtLeast(10, 14) && forJit)
            {
                flags |= MmapFlags.MAP_JIT_DARWIN;

                if (prot == (MmapProts.PROT_READ | MmapProts.PROT_WRITE))
                {
                    prot |= MmapProts.PROT_EXEC;
                }
            }

            IntPtr ptr = Mmap(IntPtr.Zero, size, prot, flags, -1, 0);

            if (ptr == MAP_FAILED)
            {
                throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
            }

            // The ownership remap is what REMOVES execute here, rather than granting it.
            //
            // Measured on tvOS 27: this path runs, every mach call returns success, and the
            // region still comes back max=rw-. An ordinary allocation on the same device
            // gets max=rwx, because Darwin gives anonymous mmap VM_PROT_ALL as its maximum
            // protection. So the plain mapping already permits execute and the memory entry
            // does not: mach_make_memory_entry_64 cannot grant VM_PROT_EXECUTE without the
            // dynamic-codesigning entitlement, which a free account cannot carry, and
            // vm_map's requested max is then clamped to what the entry allows instead of
            // failing. Skipping it leaves the mapping with max=rwx, which Commit's
            // mprotect(RW|EXEC) can then actually reach.
            //
            // The cost of skipping is the ledger tagging, so emitted code counts toward the
            // process footprint again. At ~320 MiB against a 2 GiB ceiling that is affordable.
            bool skipOwnershipRemap =
                Environment.GetEnvironmentVariable("JIT_OWNERSHIP_REMAP") == "0";

            if (forJit && (OperatingSystem.IsIOS() || OperatingSystem.IsTvOS()))
            {
                Ryujinx.Common.Logging.Logger.Notice.Print(
                    Ryujinx.Common.Logging.LogClass.Cpu,
                    $"[JITMEM] mapped 0x{ptr:X} size 0x{size:X} prot={prot} flags={flags}.");
            }

            if ((OperatingSystem.IsIOS() || OperatingSystem.IsTvOS()) && forJit && skipOwnershipRemap)
            {
                Ryujinx.Common.Logging.Logger.Notice.Print(
                    Ryujinx.Common.Logging.LogClass.Cpu,
                    $"[JITMEM] skipping ownership remap at 0x{ptr:X} size 0x{size:X}; " +
                    "relying on the mapping's own max protection.");
            }
            else if ((OperatingSystem.IsIOS() || OperatingSystem.IsTvOS()) && forJit)
            {
                // This is the only thing that makes a region executable on this platform.
                // The JIT pages come back r--/max=rw-, so either this does not run or it
                // fails; an exception here would otherwise unwind into the translator
                // constructor and be attributed to something else entirely.
                Ryujinx.Common.Logging.Logger.Notice.Print(
                    Ryujinx.Common.Logging.LogClass.Cpu,
                    $"[JITMEM] applying ownership workaround at 0x{ptr:X} size 0x{size:X} prot={prot}.");

                try
                {
                    MachJitWorkaround.ReallocateAreaWithOwnership(ptr, (int)size);

                    Ryujinx.Common.Logging.Logger.Notice.Print(
                        Ryujinx.Common.Logging.LogClass.Cpu,
                        $"[JITMEM] ownership workaround succeeded at 0x{ptr:X}.");
                }
                catch (Exception ex)
                {
                    Ryujinx.Common.Logging.Logger.Notice.Print(
                        Ryujinx.Common.Logging.LogClass.Cpu,
                        $"[JITMEM] ownership workaround FAILED at 0x{ptr:X}: {ex.Message}");

                    throw;
                }
            }
            else if (forJit)
            {
                Ryujinx.Common.Logging.Logger.Notice.Print(
                    Ryujinx.Common.Logging.LogClass.Cpu,
                    $"[JITMEM] forJit requested at 0x{ptr:X} but platform branch not taken.");
            }

            if (!_allocations.TryAdd(ptr, size))
            {
                // This should be impossible, kernel shouldn't return an already mapped address.
                throw new InvalidOperationException();
            }

            return ptr;
        }

        public static void Commit(IntPtr address, ulong size, bool forJit)
        {
            MmapProts prot = MmapProts.PROT_READ | MmapProts.PROT_WRITE;

            if (((OperatingSystem.IsIOS() || OperatingSystem.IsTvOS()) || OperatingSystem.IsMacOSVersionAtLeast(10, 14)) && forJit)
            {
                prot |= MmapProts.PROT_EXEC;
            }

            if (mprotect(address, size, prot) != 0)
            {
                throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
            }
        }

        public static void Decommit(IntPtr address, ulong size)
        {
            // Must be writable for madvise to work properly.
            if (mprotect(address, size, MmapProts.PROT_READ | MmapProts.PROT_WRITE) != 0)
            {
                throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
            }

            if (madvise(address, size, MADV_REMOVE) != 0)
            {
                throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
            }

            if (mprotect(address, size, MmapProts.PROT_NONE) != 0)
            {
                throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
            }
        }

        public static bool Reprotect(IntPtr address, ulong size, MemoryPermission permission)
        {
            return mprotect(address, size, GetProtection(permission)) == 0;
        }

        private static MmapProts GetProtection(MemoryPermission permission)
        {
            return permission switch
            {
                MemoryPermission.None => MmapProts.PROT_NONE,
                MemoryPermission.Read => MmapProts.PROT_READ,
                MemoryPermission.ReadAndWrite => MmapProts.PROT_READ | MmapProts.PROT_WRITE,
                MemoryPermission.ReadAndExecute => MmapProts.PROT_READ | MmapProts.PROT_EXEC,
                MemoryPermission.ReadWriteExecute => MmapProts.PROT_READ | MmapProts.PROT_WRITE | MmapProts.PROT_EXEC,
                MemoryPermission.Execute => MmapProts.PROT_EXEC,
                _ => throw new MemoryProtectionException(permission),
            };
        }

        public static bool Free(IntPtr address)
        {
            if (_allocations.TryRemove(address, out ulong size))
            {
                return munmap(address, size) == 0;
            }

            return false;
        }

        public static bool Unmap(IntPtr address, ulong size)
        {
            return munmap(address, size) == 0;
        }

        private static ConcurrentDictionary<IntPtr, ulong> _sharedMemorySizes = new ConcurrentDictionary<nint, ulong>();

        public unsafe static IntPtr CreateSharedMemory(ulong size, bool reserve)
        {
            int fd;

            if ((OperatingSystem.IsIOS() || OperatingSystem.IsTvOS()))
            {
                IntPtr baseAddress = MachJitWorkaround.AllocateSharedMemory(size, reserve);

                _sharedMemorySizes.TryAdd(baseAddress, size);

                return baseAddress;
            }
            else if (OperatingSystem.IsMacOS())
            {
                byte[] memName = "Ryujinx-XXXXXX"u8.ToArray();

                fixed (byte* pMemName = memName)
                {
                    fd = shm_open((IntPtr)pMemName, 0x2 | 0x200 | 0x800 | 0x400, 384); // O_RDWR | O_CREAT | O_EXCL | O_TRUNC, 0600
                    if (fd == -1)
                    {
                        throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
                    }

                    if (shm_unlink((IntPtr)pMemName) != 0)
                    {
                        throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
                    }
                }
            }
            else
            {
                byte[] fileName = "/dev/shm/Ryujinx-XXXXXX"u8.ToArray();

                fixed (byte* pFileName = fileName)
                {
                    fd = mkstemp((IntPtr)pFileName);
                    if (fd == -1)
                    {
                        throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
                    }

                    if (unlink((IntPtr)pFileName) != 0)
                    {
                        throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
                    }
                }
            }

            if (ftruncate(fd, (IntPtr)size) != 0)
            {
                throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
            }

            return fd;
        }

        public static void DestroySharedMemory(IntPtr handle)
        {
            if ((OperatingSystem.IsIOS() || OperatingSystem.IsTvOS()))
            {
                if (_sharedMemorySizes.TryGetValue(handle, out ulong size))
                {
                    _sharedMemorySizes.Remove(handle, out _);
                    MachJitWorkaround.DestroySharedMemory(handle, size);
                }
            }
            else
            {
                close(handle.ToInt32());
            }
        }

        public static IntPtr MapSharedMemory(IntPtr handle, ulong size)
        {
            if ((OperatingSystem.IsIOS() || OperatingSystem.IsTvOS()))
            {
                // The base of the shared memory is already mapped - it's the handle.
                // Views are remapped from it.

                return handle;
            }
            else
            {
                return Mmap(IntPtr.Zero, size, MmapProts.PROT_READ | MmapProts.PROT_WRITE, MmapFlags.MAP_SHARED, handle.ToInt32(), 0);
            }
        }

        public static void UnmapSharedMemory(IntPtr address, ulong size)
        {
            if (!(OperatingSystem.IsIOS() || OperatingSystem.IsTvOS()))
            {
                munmap(address, size);
            }
        }

        public static void MapView(IntPtr sharedMemory, ulong srcOffset, IntPtr location, ulong size)
        {
            if ((OperatingSystem.IsIOS() || OperatingSystem.IsTvOS()))
            {
                MachJitWorkaround.MapView(sharedMemory, srcOffset, location, size);
            }
            else
            {
                Mmap(location, size, MmapProts.PROT_READ | MmapProts.PROT_WRITE, MmapFlags.MAP_FIXED | MmapFlags.MAP_SHARED, sharedMemory.ToInt32(), (long)srcOffset);
            }
        }

        public static void UnmapView(IntPtr location, ulong size)
        {
            Mmap(location, size, MmapProts.PROT_NONE, MmapFlags.MAP_FIXED | MmapFlags.MAP_PRIVATE | MmapFlags.MAP_ANONYMOUS | MmapFlags.MAP_NORESERVE, -1, 0);
        }
    }
}

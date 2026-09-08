using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.Horizon.Common;
using System;

namespace Ryujinx.HLE.HOS.Kernel.Common
{
    static class KernelInit
    {
        private readonly struct MemoryRegion
        {
            public ulong Address { get; }
            public ulong Size { get; }

            public ulong EndAddress => Address + Size;

            public MemoryRegion(ulong address, ulong size)
            {
                Address = address;
                Size = size;
            }
        }

        public static void InitializeResourceLimit(KResourceLimit resourceLimit, MemorySize size)
        {
            static void EnsureSuccess(Result result)
            {
                if (result != Result.Success)
                {
                    throw new InvalidOperationException($"Unexpected result \"{result}\".");
                }
            }

            ulong ramSize = KSystemControl.GetDramSize(size);

            EnsureSuccess(resourceLimit.SetLimitValue(LimitableResource.Memory, (long)ramSize));
            EnsureSuccess(resourceLimit.SetLimitValue(LimitableResource.Thread, 800));
            EnsureSuccess(resourceLimit.SetLimitValue(LimitableResource.Event, 700));
            EnsureSuccess(resourceLimit.SetLimitValue(LimitableResource.TransferMemory, 200));
            EnsureSuccess(resourceLimit.SetLimitValue(LimitableResource.Session, 900));

            if (!resourceLimit.Reserve(LimitableResource.Memory, 0) ||
                !resourceLimit.Reserve(LimitableResource.Memory, 0x60000))
            {
                throw new InvalidOperationException("Unexpected failure reserving memory on resource limit.");
            }
        }

        public static KMemoryRegionManager[] GetMemoryRegions(MemorySize size, MemoryArrange arrange)
        {
            ulong poolEnd = KSystemControl.GetDramEndAddress(size);
            ulong applicationPoolSize = KSystemControl.GetApplicationPoolSize(arrange);
            ulong appletPoolSize = KSystemControl.GetAppletPoolSize(arrange);

            MemoryRegion servicePool;
            MemoryRegion nvServicesPool;
            MemoryRegion appletPool;
            MemoryRegion applicationPool;

            ulong nvServicesPoolSize = KSystemControl.GetMinimumNonSecureSystemPoolSize();

            // The pools are carved from the top of DRAM downwards and the service pool
            // absorbs whatever is left, so an application pool that is too large makes
            // that subtraction underflow into an enormous bogus service pool instead of
            // failing. Since the size is now settable from the command line, bound it.
            const ulong MiB = 1024 * 1024;
            const ulong MinimumServicePoolSize = 64 * MiB;
            const ulong MinimumApplicationPoolSize = 128 * MiB;

            ulong reservedBelowApplication =
                DramMemoryMap.SlabHeapEnd + appletPoolSize + nvServicesPoolSize + MinimumServicePoolSize;

            ulong maxApplicationPoolSize = poolEnd > reservedBelowApplication
                ? poolEnd - reservedBelowApplication
                : 0;

            if (applicationPoolSize > maxApplicationPoolSize)
            {
                Logger.Warning?.Print(LogClass.Kernel,
                    $"Application pool of {applicationPoolSize / MiB} MiB does not fit; clamping to {maxApplicationPoolSize / MiB} MiB.");

                applicationPoolSize = maxApplicationPoolSize;
            }

            if (applicationPoolSize < MinimumApplicationPoolSize)
            {
                Logger.Warning?.Print(LogClass.Kernel,
                    $"Application pool of {applicationPoolSize / MiB} MiB is below the {MinimumApplicationPoolSize / MiB} MiB floor; raising it.");

                applicationPoolSize = MinimumApplicationPoolSize;
            }

            applicationPool = new MemoryRegion(poolEnd - applicationPoolSize, applicationPoolSize);

            ulong nvServicesPoolEnd = applicationPool.Address - appletPoolSize;

            nvServicesPool = new MemoryRegion(nvServicesPoolEnd - nvServicesPoolSize, nvServicesPoolSize);
            appletPool = new MemoryRegion(nvServicesPoolEnd, appletPoolSize);

            // Note: There is an extra region used by the kernel, however
            // since we are doing HLE we are not going to use that memory, so give all
            // the remaining memory space to services.
            ulong servicePoolSize = nvServicesPool.Address - DramMemoryMap.SlabHeapEnd;

            servicePool = new MemoryRegion(DramMemoryMap.SlabHeapEnd, servicePoolSize);

            Logger.Notice.Print(LogClass.Kernel,
                $"Guest memory pools: application {applicationPoolSize / MiB} MiB, applet {appletPoolSize / MiB} MiB, " +
                $"service {servicePoolSize / MiB} MiB, nvservices {nvServicesPoolSize / MiB} MiB (DRAM {(poolEnd - DramMemoryMap.DramBase) / MiB} MiB)");

            return new[]
            {
                GetMemoryRegion(applicationPool),
                GetMemoryRegion(appletPool),
                GetMemoryRegion(servicePool),
                GetMemoryRegion(nvServicesPool),
            };
        }

        private static KMemoryRegionManager GetMemoryRegion(MemoryRegion region)
        {
            return new KMemoryRegionManager(region.Address, region.Size, region.EndAddress);
        }
    }
}

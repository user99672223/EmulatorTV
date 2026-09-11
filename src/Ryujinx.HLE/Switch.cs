using Ryujinx.Audio.Backends.CompatLayer;
using Ryujinx.Audio.Backends.DelayLayer;
using Ryujinx.Audio.Integration;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.SystemInfo;
using Ryujinx.Graphics.Gpu;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.HOS.Services.Apm;
using Ryujinx.HLE.HOS.Services.Hid;
using Ryujinx.HLE.Loaders.Processes;
using Ryujinx.HLE.UI;
using Ryujinx.Memory;
using System;

namespace Ryujinx.HLE
{
    public class Switch : IDisposable
    {
        public HLEConfiguration Configuration { get; }
        public IHardwareDeviceDriver AudioDeviceDriver { get; }
        public MemoryBlock Memory { get; }
        public GpuContext Gpu { get; }
        public VirtualFileSystem FileSystem { get; }
        public HOS.Horizon System { get; }
        public ProcessLoader Processes { get; }
        public PerformanceStatistics Statistics { get; }
        public Hid Hid { get; }
        public TamperMachine TamperMachine { get; }
        public IHostUIHandler UIHandler { get; }

        public bool EnableDeviceVsync { get; set; } = true;

        public bool IsFrameAvailable => Gpu.Window.IsFrameAvailable;

        public Switch(HLEConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration.GpuRenderer);
            ArgumentNullException.ThrowIfNull(configuration.AudioDeviceDriver);
            ArgumentNullException.ThrowIfNull(configuration.UserChannelPersistence);

            Configuration = configuration;
            FileSystem = Configuration.VirtualFileSystem;
            UIHandler = Configuration.HostUIHandler;

            // Mirrorable was tried here and reverted.
            //
            // The idea was that host-tracked mode would map guest ranges as views of
            // this block rather than allocating fresh memory per range. It does not:
            // AddressSpacePartition.Map always takes MappingType.Private, so there is
            // no view path for the flag to enable. All it did was route the 2 GiB DRAM
            // block through AllocateSharedMemory, moving it into the same constrained
            // hole the guest allocates from.
            //
            // Measured on device: the running total at refusal went from 1179 MiB to
            // 3163 MiB, and the game got no further either way.
            MemoryAllocationFlags memoryAllocationFlags = MemoryAllocationFlags.Reserve;

#pragma warning disable IDE0055 // Disable formatting
            AudioDeviceDriver = AddAudioCompatLayers(Configuration.AudioDeviceDriver);
            Memory            = new MemoryBlock(Configuration.MemoryConfiguration.ToDramSize(), memoryAllocationFlags);

            // Measurement point 2 of 3. The block above is a reservation of address
            // space, not committed memory, so on a healthy host this should barely move
            // the needle; if it does, the address space is being committed eagerly and
            // that is the problem to fix before tuning pool sizes.
            AppleMemoryProbe.Log("guest pool reserved");

            Gpu               = new GpuContext(Configuration.GpuRenderer);
            System            = new HOS.Horizon(this);
            Statistics        = new PerformanceStatistics();
            Hid               = new Hid(this, System.HidStorage);
            Processes         = new ProcessLoader(this);
            TamperMachine     = new TamperMachine();

            System.InitializeServices();
            System.State.SetLanguage(Configuration.SystemLanguage);
            System.State.SetRegion(Configuration.Region);

            EnableDeviceVsync                       = Configuration.EnableVsync;
            System.State.DockedMode                 = Configuration.EnableDockedMode;
            System.PerformanceState.PerformanceMode = System.State.DockedMode ? PerformanceMode.Boost : PerformanceMode.Default;
            System.EnablePtc                        = Configuration.EnablePtc;
            System.FsIntegrityCheckLevel            = Configuration.FsIntegrityCheckLevel;
            System.GlobalAccessLogMode              = Configuration.FsGlobalAccessLogMode;
#pragma warning restore IDE0055
        }

        private IHardwareDeviceDriver AddAudioCompatLayers(IHardwareDeviceDriver driver)
        {
            ulong sampleDelay = (OperatingSystem.IsIOS() || OperatingSystem.IsTvOS()) ? 1024ul : 0;
            driver = new CompatLayerHardwareDeviceDriver(driver);

            if (sampleDelay > 0)
            {
                driver = new DelayLayerHardwareDeviceDriver(driver, sampleDelay);
            }

            return driver;
        }

        public bool LoadCart(string exeFsDir, string romFsFile = null)
        {
            return Processes.LoadUnpackedNca(exeFsDir, romFsFile);
        }

        public bool LoadXci(string xciFile, ulong applicationId = 0)
        {
            return Processes.LoadXci(xciFile, applicationId);
        }

        public bool LoadNca(string ncaFile)
        {
            return Processes.LoadNca(ncaFile);
        }

        public bool LoadNsp(string nspFile, ulong applicationId = 0)
        {
            return Processes.LoadNsp(nspFile, applicationId);
        }

        public bool LoadProgram(string fileName)
        {
            return Processes.LoadNxo(fileName);
        }

        public bool WaitFifo()
        {
            return Gpu.GPFifo.WaitForCommands();
        }

        public void ProcessFrame()
        {
            Gpu.ProcessShaderCacheQueue();
            Gpu.Renderer.PreFrame();
            Gpu.GPFifo.DispatchCalls();
        }

        public bool ConsumeFrameAvailable()
        {
            return Gpu.Window.ConsumeFrameAvailable();
        }

        public void PresentFrame(Action swapBuffersCallback)
        {
            Gpu.Window.Present(swapBuffersCallback);
        }

        public void SetVolume(float volume)
        {
            AudioDeviceDriver.Volume = Math.Clamp(volume, 0f, 1f);
        }

        public float GetVolume()
        {
            return AudioDeviceDriver.Volume;
        }

        public void EnableCheats()
        {
            ModLoader.EnableCheats(Processes.ActiveApplication.ProgramId, TamperMachine);
        }

        public bool IsAudioMuted()
        {
            return AudioDeviceDriver.Volume == 0;
        }

        public void DisposeGpu()
        {
            Gpu.Dispose();
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                System.Dispose();
                AudioDeviceDriver.Dispose();
                FileSystem.Dispose();
                Memory.Dispose();
            }
        }
    }
}

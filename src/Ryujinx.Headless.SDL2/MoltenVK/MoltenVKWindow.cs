using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Input.HLE;
using Ryujinx.SDL2.Common;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static SDL2.SDL;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Ryujinx.Headless.SDL2.Vulkan
{
    class MoltenVKWindow : WindowBase
    {
        public IntPtr nativeMetalLayer = IntPtr.Zero;
        
        private Vk _vk;
        private ExtMetalSurface _metalSurface;
        private SurfaceKHR _surface;
        private bool _surfaceCreated;

        public MoltenVKWindow(
            InputManager inputManager,
            GraphicsDebugLevel glLogLevel,
            AspectRatio aspectRatio,
            bool enableMouse,
            HideCursorMode hideCursorMode) : base(inputManager, glLogLevel, aspectRatio, enableMouse, hideCursorMode)
        {
            _vk = Vk.GetApi();
            _surfaceCreated = false;
        }

        public override SDL_WindowFlags GetWindowFlags() => SDL_WindowFlags.SDL_WINDOW_VULKAN;

        protected override void InitializeWindowRenderer() {}

        protected override void InitializeRenderer()
        {
            if (IsExclusiveFullscreen)
            {
                Renderer?.Window.SetSize(ExclusiveFullscreenWidth, ExclusiveFullscreenHeight);
                MouseDriver.SetClientSize(ExclusiveFullscreenWidth, ExclusiveFullscreenHeight);
            }
            else
            {
                Renderer?.Window.SetSize(DefaultWidth, DefaultHeight);
                MouseDriver.SetClientSize(DefaultWidth, DefaultHeight);
            }
        }

        public void SetNativeWindow(IntPtr metalLayer)
        {
            if (metalLayer == IntPtr.Zero)
            {
                return;
            }
            nativeMetalLayer = IntPtr.Zero;
            nativeMetalLayer = metalLayer;
        }

        private static void BasicInvoke(Action action)
        {
            action();
        }

        public unsafe IntPtr CreateWindowSurface(IntPtr instanceHandle)
        {
            if (_surfaceCreated)
            {
                return (IntPtr)(ulong)_surface.Handle;
            }

            // The layer is copied into this instance once, in LoadApplication, which
            // can run before the host app has produced its CAMetalLayer -- and
            // SetNativeWindow returns early on a zero, so the copy is never retried.
            // Re-read the live value instead of trusting that one-shot copy, and give
            // the UI a moment to hand it over rather than throwing on a race. This
            // exception is raised on the render thread, where it is unhandled and takes
            // the whole process down.
            if (nativeMetalLayer == IntPtr.Zero)
            {
                for (int attempt = 0; attempt < 100 && nativeMetalLayer == IntPtr.Zero; attempt++)
                {
                    nativeMetalLayer = Program.GetNativeMetalLayer();

                    if (nativeMetalLayer == IntPtr.Zero)
                    {
                        System.Threading.Thread.Sleep(50);
                    }
                }

                if (nativeMetalLayer != IntPtr.Zero)
                {
                    Logger.Info?.Print(LogClass.Application, "Picked up the CAMetalLayer after waiting for the host view.");
                }
            }

            if (nativeMetalLayer == IntPtr.Zero)
            {
                throw new Exception("Cannot create Vulkan surface: No CAMetalLayer set");
            }

            var instance = new Instance((nint)instanceHandle);  
            if (!_vk.TryGetInstanceExtension(instance, out _metalSurface))
            {
                throw new Exception("Failed to get ExtMetalSurface extension");
            }

            var createInfo = new MetalSurfaceCreateInfoEXT
            {
                SType = StructureType.MetalSurfaceCreateInfoExt,
                PNext = null,
                PLayer = (nint*)nativeMetalLayer
            };

            SurfaceKHR* surfacePtr = stackalloc SurfaceKHR[1];  
            Result result = _metalSurface.CreateMetalSurface(instance, &createInfo, null, surfacePtr);
            if (result != Result.Success)
            {
                throw new Exception($"vkCreateMetalSurfaceEXT failed with error code {result}");
            }

            _surface = *surfacePtr; 
            _surfaceCreated = true;

            return (IntPtr)(ulong)_surface.Handle;
        }

        public unsafe string[] GetRequiredInstanceExtensions()
        {
            List<string> requiredExtensions = new List<string>
            {
                "VK_KHR_surface",
                "VK_EXT_metal_surface"
            };
            
            uint extensionCount = 0;
            _vk.EnumerateInstanceExtensionProperties((byte*)null, &extensionCount, null);
            
            if (extensionCount == 0)
            {
                string errorMessage = "Failed to enumerate Vulkan instance extensions";
                Logger.Error?.Print(LogClass.Application, errorMessage);
                throw new Exception(errorMessage);
            }

            ExtensionProperties* extensions = stackalloc ExtensionProperties[(int)extensionCount];
            
            Result result = _vk.EnumerateInstanceExtensionProperties((byte*)null, &extensionCount, extensions);
            
            if (result != Result.Success)
            {
                string errorMessage = $"Failed to enumerate Vulkan instance extensions, error: {result}";
                Logger.Error?.Print(LogClass.Application, errorMessage);
                throw new Exception(errorMessage);
            }

            List<string> availableExtensions = new List<string>();

            for (int i = 0; i < extensionCount; i++)
            {
                string extName = Marshal.PtrToStringAnsi((IntPtr)extensions[i].ExtensionName);
                availableExtensions.Add(extName);
            }

            Logger.Info?.Print(LogClass.Application, $"Available Vulkan extensions: {string.Join(", ", availableExtensions)}");
            
            foreach (string requiredExt in requiredExtensions)
            {
                if (!availableExtensions.Contains(requiredExt))
                {
                    string errorMessage = $"Required Vulkan extension {requiredExt} is not available";
                    Logger.Error?.Print(LogClass.Application, errorMessage);
                }
            }

            Logger.Info?.Print(LogClass.Application, $"Using Vulkan extensions: {string.Join(", ", requiredExtensions)}");

            return requiredExtensions.ToArray();
        }

        protected override void FinalizeWindowRenderer()
        {
            if (_surfaceCreated)
            {
                _surface = default;
                _surfaceCreated = false;
            }
            
            nativeMetalLayer = IntPtr.Zero;
        }


        protected override void SwapBuffers() {}
    }
}

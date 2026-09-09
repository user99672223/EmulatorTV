using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Ryujinx.Headless.SDL2
{
    /// <summary>
    /// Resolves the bundled native libraries by absolute path on Apple platforms.
    ///
    /// Inside an app bundle these live in Frameworks/, and their install names are
    /// @rpath-relative. A library that the app links is loaded by dyld at launch, so
    /// a later DllImport of its bare name resolves against the already-loaded image.
    /// One that is only embedded is never loaded, and dlopen of a bare leaf name
    /// searches the system paths rather than the bundle -- which is why
    /// DllImport("SDL2") failed with a DllNotFoundException whose dyld error was
    /// empty: no candidate path was ever attempted.
    ///
    /// Mapping the names to real paths fixes it without depending on which libraries
    /// happen to be on the link line.
    /// </summary>
    internal static class AppleNativeLibraries
    {
        private static bool _registered;

        private static readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase)
        {
            ["SDL2"] = "SDL2.framework/SDL2",
            ["libSDL2.dylib"] = "SDL2.framework/SDL2",
            ["MoltenVK"] = "libMoltenVK.dylib",
            ["libMoltenVK.dylib"] = "libMoltenVK.dylib",
            // Silk.NET asks for Vulkan; on Apple platforms MoltenVK is the implementation.
            ["vulkan"] = "libMoltenVK.dylib",
            ["libvulkan"] = "libMoltenVK.dylib",
            ["libvulkan.dylib"] = "libMoltenVK.dylib",
            ["libvulkan.1.dylib"] = "libMoltenVK.dylib",
            // Some assemblies import by bundle-relative path rather than by bare
            // name; map those to the same absolute locations.
            ["SDL2.framework/SDL2"] = "SDL2.framework/SDL2",
            ["RyujinxHelper.framework/RyujinxHelper"] = "RyujinxHelper.framework/RyujinxHelper",
            // Ryujinx.Memory imports this for the JIT allocation traps. Without the
            // mapping the DllImport does not resolve, the allocator's catch replaces the
            // guest function with NOP;RET, and the guest faults jumping into a JIT page
            // that was never made executable -- which reads as a crash a long way from
            // the actual cause.
            ["BreakpointJIT.framework/BreakpointJIT"] = "BreakpointJIT.framework/BreakpointJIT",
        };

        public static void Register()
        {
            if (_registered || !(OperatingSystem.IsIOS() || OperatingSystem.IsTvOS()))
            {
                return;
            }

            _registered = true;

            string frameworks = FindFrameworksDirectory();

            if (frameworks == null)
            {
                Logger.Warning?.Print(LogClass.Application, "Could not locate the bundle's Frameworks directory; native libraries will use default probing.");

                return;
            }

            Logger.Info?.Print(LogClass.Application, $"Native library directory: {frameworks}");

            DllImportResolver resolver = (name, assembly, searchPath) => Resolve(name, frameworks);

            // The import lives in whichever assembly declares it, so the resolver has
            // to be attached to each of them rather than to this one.
            // global:: is required: the enclosing namespace is Ryujinx.Headless.SDL2,
            // so a bare SDL2.SDL binds to Ryujinx.Headless.SDL2.SDL and fails.
            TryRegister(typeof(global::SDL2.SDL).Assembly, resolver, "SDL2");
            TryRegister(typeof(Graphics.Vulkan.MoltenVK.MVKInitialization).Assembly, resolver, "Ryujinx.Graphics.Vulkan");
            TryRegister(typeof(Silk.NET.Vulkan.Vk).Assembly, resolver, "Silk.NET.Vulkan");
            TryRegister(typeof(AppleNativeLibraries).Assembly, resolver, "Ryujinx.Headless.SDL2");

            // These import bundled libraries too, and a resolver only applies to the
            // assembly it is registered on. Audio.Backends.SDL2 imports
            // "SDL2.framework/SDL2"; Common, HLE and Input import RyujinxHelper.
            TryRegister(typeof(Audio.Backends.SDL2.SDL2HardwareDeviceDriver).Assembly, resolver, "Ryujinx.Audio.Backends.SDL2");
            TryRegister(typeof(Common.Logging.Logger).Assembly, resolver, "Ryujinx.Common");
            TryRegister(typeof(HLE.Switch).Assembly, resolver, "Ryujinx.HLE");
            TryRegister(typeof(Input.IGamepad).Assembly, resolver, "Ryujinx.Input");
            TryRegister(typeof(Memory.MemoryBlock).Assembly, resolver, "Ryujinx.Memory");

            RegisterSilkVulkanResolver(frameworks);

            // Vk.GetApi() is reached from the MoltenVKWindow constructor, which runs
            // before VulkanRenderer calls MVKInitialization.Initialize -- the only
            // MoltenVK import that goes through the resolver above. Loading the image
            // here means that even a bare dlopen("libMoltenVK.dylib") matches an
            // already-loaded image, whichever path asks for it first.
            string moltenVk = Path.Combine(frameworks, "libMoltenVK.dylib");

            if (NativeLibrary.TryLoad(moltenVk, out _))
            {
                Logger.Info?.Print(LogClass.Application, "Preloaded MoltenVK.");
            }
            else
            {
                Logger.Warning?.Print(LogClass.Application, $"Could not preload MoltenVK from {moltenVk}");
            }
        }

        /// <summary>
        /// Silk.NET does not load through DllImport, so a DllImportResolver never sees
        /// its requests: Vk.GetApi() goes through Silk.NET.Core.Loader.PathResolver,
        /// which probes AppContext.BaseDirectory and the main module's directory. Both
        /// are the bundle root, while the library is in Frameworks/, so every candidate
        /// misses. This points its resolver straight at the bundled MoltenVK.
        /// </summary>
        private static void RegisterSilkVulkanResolver(string frameworks)
        {
            try
            {
                string moltenVk = Path.Combine(frameworks, "libMoltenVK.dylib");

                if (Silk.NET.Core.Loader.PathResolver.Default is Silk.NET.Core.Loader.DefaultPathResolver def)
                {
                    def.Resolvers.Insert(0, name =>
                    {
                        string leaf = Path.GetFileName(name);

                        return leaf is "libvulkan.dylib" or "libvulkan.1.dylib" or "libvulkan.so.1"
                            or "libMoltenVK.dylib" or "vulkan" or "vulkan-1.dll"
                            ? new[] { moltenVk }
                            : Array.Empty<string>();
                    });

                    Logger.Info?.Print(LogClass.Application, "Installed a Silk.NET Vulkan path resolver.");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Could not install the Silk.NET Vulkan resolver: {ex.Message}");
            }
        }

        private static void TryRegister(Assembly assembly, DllImportResolver resolver, string label)
        {
            try
            {
                NativeLibrary.SetDllImportResolver(assembly, resolver);
            }
            catch (InvalidOperationException)
            {
                // Already set for this assembly; harmless.
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Could not install a native library resolver for {label}: {ex.Message}");
            }
        }

        private static IntPtr Resolve(string name, string frameworks)
        {
            if (!_map.TryGetValue(name, out string relative))
            {
                return IntPtr.Zero;   // fall back to the default probing
            }

            string full = Path.Combine(frameworks, relative);

            if (NativeLibrary.TryLoad(full, out IntPtr handle))
            {
                return handle;
            }

            Logger.Warning?.Print(LogClass.Application, $"Native library '{name}' not loadable at {full}");

            return IntPtr.Zero;
        }

        private static string FindFrameworksDirectory()
        {
            // <bundle>/MeloTV -> <bundle>/Frameworks
            string exe = Environment.ProcessPath;

            if (!string.IsNullOrEmpty(exe))
            {
                string dir = Path.GetDirectoryName(exe);

                if (dir != null && Directory.Exists(Path.Combine(dir, "Frameworks")))
                {
                    return Path.Combine(dir, "Frameworks");
                }
            }

            // This dylib is itself inside Frameworks/, so the base directory may
            // already be the right place.
            string baseDir = AppContext.BaseDirectory;

            if (!string.IsNullOrEmpty(baseDir))
            {
                if (Directory.Exists(Path.Combine(baseDir, "Frameworks")))
                {
                    return Path.Combine(baseDir, "Frameworks");
                }

                if (File.Exists(Path.Combine(baseDir, "libMoltenVK.dylib")))
                {
                    return baseDir;
                }
            }

            return null;
        }
    }
}

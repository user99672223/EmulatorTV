using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Memory
{
    /// <summary>
    /// Writes JIT code by asking the attached debugger to do it.
    /// </summary>
    /// <remarks>
    /// On a device with a Trusted Execution Monitor the app cannot produce an
    /// executable page even when it holds a writable alias of one. Measured inside a
    /// single 4 MiB region the debugger allocated rx: a page written by the app
    /// through its vm_remap rw alias faults EXC_BAD_ACCESS on instruction fetch,
    /// while a page written by the debugger executes and reports EXC_BREAKPOINT.
    /// Both read back the emitted bytes correctly through the rx view, so the
    /// difference is invisible to every check except running the code.
    ///
    /// Writing through the alias is therefore not a cheaper path to the same place;
    /// it is what destroys the page. The code is staged in ordinary memory here and
    /// the debugger copies it in.
    /// </remarks>
    public static class DebuggerJitPublisher
    {
        [DllImport("BreakpointJIT.framework/BreakpointJIT", EntryPoint = "BreakPublishJIT")]
        private static extern unsafe void BreakPublishJIT(byte* destination, byte* source, nuint length);

        private static readonly object _lock = new();

        private static unsafe byte* _staging;
        private static int _stagingSize;

        /// <summary>
        /// Whether JIT code has to be published through the debugger rather than
        /// written directly.
        /// </summary>
        public static bool Required => DualMappedJitAllocator.hasTXM;

        /// <summary>
        /// Places <paramref name="code"/> at <paramref name="destination"/>, which is
        /// an address inside a region the debugger allocated.
        /// </summary>
        public static unsafe void Publish(IntPtr destination, ReadOnlySpan<byte> code)
        {
            if (code.IsEmpty)
            {
                return;
            }

            // The debugger reads the source out of the target's memory, so it needs a
            // fixed address that stays put for the duration of the trap. A pinned
            // managed buffer would do, but a single native staging buffer avoids
            // pinning on the hot path and keeps the address stable across calls.
            lock (_lock)
            {
                if (_stagingSize < code.Length)
                {
                    int size = Math.Max(code.Length, 64 * 1024);

                    byte* grown = (byte*)NativeMemory.Alloc((nuint)size);

                    if (_staging != null)
                    {
                        NativeMemory.Free(_staging);
                    }

                    _staging = grown;
                    _stagingSize = size;
                }

                code.CopyTo(new Span<byte>(_staging, code.Length));

                BreakPublishJIT((byte*)destination, _staging, (nuint)code.Length);
            }
        }
    }
}

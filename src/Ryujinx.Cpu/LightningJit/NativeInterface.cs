using ARMeilleure.Memory;
using Ryujinx.Common.Logging;
using Ryujinx.Cpu.LightningJit.State;
using System;

namespace Ryujinx.Cpu.LightningJit
{
    static class NativeInterface
    {
        private const int DczSizeLog2 = 4; // Log2 size in words
        private const int DczSizeInBytes = 4 << DczSizeLog2;

        private class ThreadContext
        {
            public ExecutionContext Context { get; }
            public IMemoryManager Memory { get; }
            public Translator Translator { get; }

            public ThreadContext(ExecutionContext context, IMemoryManager memory, Translator translator)
            {
                Context = context;
                Memory = memory;
                Translator = translator;
            }
        }

        [ThreadStatic]
        private static ThreadContext Context;

        public static void RegisterThread(ExecutionContext context, IMemoryManager memory, Translator translator)
        {
            Context = new ThreadContext(context, memory, translator);
        }

        public static void UnregisterThread()
        {
            Context = null;
        }

        public static void Break(ulong address, int imm)
        {
            GetContext().OnBreak(address, imm);
        }

        public static void SupervisorCall(ulong address, int imm)
        {
            GetContext().OnSupervisorCall(address, imm);
        }

        public static void Undefined(ulong address, int opCode)
        {
            GetContext().OnUndefined(address, opCode);
        }

        public static ulong GetCntfrqEl0()
        {
            return GetContext().CntfrqEl0;
        }

        public static ulong GetCntpctEl0()
        {
            return GetContext().CntpctEl0;
        }

        private static int _loggedFirstFunctionAddress;

        /// <summary>
        /// Native entry point for the generated dispatch stubs.
        ///
        /// The stubs previously reached managed code through a delegate wrapped by
        /// Marshal.GetFunctionPointerForDelegate. That produces a reverse-P/Invoke thunk,
        /// which NativeAOT has to synthesise, and on this build the guest never arrives:
        /// the fallback path branches to that pointer and the process spins at 100% of a
        /// core without a single managed frame running. UnmanagedCallersOnly is compiled
        /// into an ordinary exported function instead, so the address in the emitted code
        /// is the function itself.
        /// </summary>
        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        public static ulong GetFunctionAddressNative(IntPtr framePointer, ulong address)
        {
            return GetFunctionAddress(framePointer, address);
        }

        public static ulong GetFunctionAddress(IntPtr framePointer, ulong address)
        {
            // The generated dispatch stub reaches managed code only through this
            // reverse-P/Invoke thunk. If the emulator wedges after entering the dispatch
            // loop and this never fires, the hang is entirely inside native dispatch and
            // the managed translation path was never involved.
            if (System.Threading.Interlocked.Exchange(ref _loggedFirstFunctionAddress, 1) == 0)
            {
                Logger.Notice.Print(LogClass.Cpu,
                    $"Native dispatch reached managed GetFunctionAddress for 0x{address:X}.");
            }

            return (ulong)Context.Translator.GetOrTranslatePointer(framePointer, address, GetContext().ExecutionMode);
        }

        public static void InvalidateJitCacheRegion(ulong address, ulong size)
        {
            Context.Translator.InvalidateJitCacheRegion(address, size);
        }

        public static void InvalidateCacheLine(ulong address)
        {
            Context.Translator.InvalidateJitCacheRegion(address, DczSizeInBytes);
        }

        public static void SetPageTablePointer() 
        {
            Context.Context.SetPageTablePointer(Context.Memory);
        }

        public static bool CheckSynchronization()
        {
            ExecutionContext context = GetContext();

            context.CheckInterrupt();

            return context.Running;
        }

        public static ExecutionContext GetContext()
        {
            return Context.Context;
        }

        public static IMemoryManager GetMemoryManager()
        {
            return Context.Memory;
        }
    }
}

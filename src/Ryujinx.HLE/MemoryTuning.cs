namespace Ryujinx.HLE
{
    /// <summary>
    /// Host-side overrides for the guest memory layout.
    ///
    /// These exist because the stock memory arrangements assume a real Switch, which
    /// hands an application 3285 MiB. On a memory constrained host (an Apple TV app is
    /// capped at roughly 2 GiB for the whole process, emulator included) a guest that
    /// believes it has 3285 MiB will size its heaps accordingly and be killed by the OS
    /// long before it runs out of guest memory. Reporting a smaller pool makes the guest
    /// size itself to fit.
    /// </summary>
    public static class MemoryTuning
    {
        /// <summary>
        /// Overrides the application memory pool size, in bytes. When null the size
        /// comes from the selected <see cref="MemoryConfiguration"/> as usual.
        ///
        /// Set this before constructing <see cref="Switch"/>; it is read once during
        /// kernel initialisation.
        /// </summary>
        public static ulong? ApplicationPoolSizeBytes { get; set; }
    }
}

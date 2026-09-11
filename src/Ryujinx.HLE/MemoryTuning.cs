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

        /// <summary>
        /// Overrides the applet memory pool size, in bytes. When null the size comes
        /// from the selected <see cref="MemoryConfiguration"/> as usual.
        ///
        /// The stock arrangement reserves 507 MiB for system applets layered over a
        /// running game -- the home menu overlay, the software keyboard. A single
        /// game running offline never uses any of it, and on a constrained host that
        /// is 507 MiB the application cannot have. Lowering this moves the space to
        /// the application pool without enlarging DRAM, so it costs no address space.
        /// </summary>
        public static ulong? AppletPoolSizeBytes { get; set; }

        /// <summary>
        /// Overrides the emulated DRAM size, in bytes. When null the size comes from
        /// the selected <see cref="MemoryConfiguration"/> as usual.
        ///
        /// This is a virtual-address concern rather than a resident-memory one. The
        /// guest DRAM is reserved as one MemoryBlock, and on a host without the
        /// extended-virtual-addressing entitlement the address space runs out well
        /// before physical memory does: a 4 GiB reservation plus the JIT caches plus
        /// the host-tracked page table exceeded the limit and mmap returned ENOMEM
        /// while 2 GB of jetsam headroom was still free.
        /// </summary>
        public static ulong? DramSizeBytes { get; set; }
    }
}

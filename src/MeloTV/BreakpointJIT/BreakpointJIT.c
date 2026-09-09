// Replaces the prebuilt, iOS-only BreakpointJIT.framework.
//
// This device grants executable memory to a debugger but never to the app itself.
// Measured: mprotect adding execute to a written page is silently refused and
// permanently strips execute from that page's maximum protection; MAP_JIT returns
// EPERM; the mach memory-entry route clamps execute away; and even a page created
// PROT_READ|PROT_EXEC and never written through faults with EXC_BAD_ACCESS on the
// first instruction fetch. A region the debugger allocates rx does execute --
// confirmed by a BRK returning SIGTRAP / EXC_BREAKPOINT at the region address,
// against an rw control that returned EXC_BAD_ACCESS.
//
// So the app asks and the debugger answers. These stubs are the asking half: they
// trap, the attached script services the trap, writes the region address into x0
// and steps the pc past the BRK, and the stub returns that address to its caller.
//
// The encodings and the x16 selector convention come from the original binary's
// symbol table, so the two halves agree: BreakGetJITMapping is brk #0xF00D with
// x16=1, BreakJITDetach the same trap with x16=0, BreakMarkJITMapping brk #0x69.
//
// Written as module-level assembly inside a .c rather than a .s: a .s file was not
// routed into the compile phase and the framework built without these symbols.
//
// Nothing here works without the script attached and serving. Without it the BRK
// raises SIGTRAP with no handler and the process stops there.

__asm__(
    ".text\n"
    ".align 2\n"

    // void *BreakGetJITMapping(void *addr, size_t bytes)
    //   x0 = addr (hint, may be null), x1 = bytes -> x0 = region
    ".globl _BreakGetJITMapping\n"
    "_BreakGetJITMapping:\n"
    "    movz x16, #1\n"
    "    brk  #0xF00D\n"
    "    ret\n"

    // void BreakJITDetach(void)
    ".globl _BreakJITDetach\n"
    "_BreakJITDetach:\n"
    "    movz x16, #0\n"
    "    brk  #0xF00D\n"
    "    ret\n"

    // void *BreakMarkJITMapping(size_t bytes)
    //   x0 = bytes -> x0 = region. The allocator tries this one first.
    ".globl _BreakMarkJITMapping\n"
    "_BreakMarkJITMapping:\n"
    "    brk  #0x69\n"
    "    ret\n"

    // void BreakPublishJIT(void *dst, const void *src, size_t len)
    //   x0 = destination inside the debugger's rx region
    //   x1 = source, ordinary readable memory
    //   x2 = byte count
    //
    // Getting the region is not enough: a page stays executable only while
    // the app does not write to it. Measured on this device, inside ONE 4 MiB
    // region the debugger allocated rx -- a page the app wrote through its own
    // vm_remap rw alias faults EXC_BAD_ACCESS on instruction fetch, while a
    // page the debugger wrote with an M packet executes and reports
    // EXC_BREAKPOINT. The rx view reads back the correct bytes either way, so
    // nothing short of running the code tells them apart.
    //
    // So the app stages the code in ordinary memory and asks the debugger to
    // place it. That is the only write path that leaves a page executable.
    ".globl _BreakPublishJIT\n"
    "_BreakPublishJIT:\n"
    "    brk  #0x70\n"
    "    ret\n"
);

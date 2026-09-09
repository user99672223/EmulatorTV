// Replaces the prebuilt, iOS-only BreakpointJIT.framework.
//
// This device grants executable memory to a debugger but never to the app itself.
// Measured: mprotect adding execute to a written page is silently refused and
// permanently strips execute from the page's maximum protection; MAP_JIT returns
// EPERM; the mach memory-entry route clamps execute away; and even a page created
// PROT_READ|PROT_EXEC, never written through, faults with EXC_BAD_ACCESS on the
// first instruction fetch. A region the debugger allocates rx executes: verified
// with a BRK that returned SIGTRAP / EXC_BREAKPOINT at the region address, against
// an rw control that returned EXC_BAD_ACCESS.
//
// So the app asks and the debugger answers. These stubs are the asking half: they
// trap, the attached script services the trap, writes the region address into x0
// and steps the pc past the BRK, and the stub returns that address to its caller.
// The instruction encodings and the x16 selector convention are taken from the
// original iOS binary's symbol table so the two halves agree.
//
// Nothing here can work without the script attached and listening. Without it the
// BRK raises SIGTRAP with no handler and the process stops.

.text
.align 2

// void *BreakGetJITMapping(void *addr, size_t bytes)
//   x0 = addr (hint, may be null), x1 = bytes -> x0 = region
.global _BreakGetJITMapping
_BreakGetJITMapping:
    movz    x16, #1
    brk     #0xF00D
    ret

// void BreakJITDetach(void)
.global _BreakJITDetach
_BreakJITDetach:
    movz    x16, #0
    brk     #0xF00D
    ret

// void *BreakMarkJITMapping(size_t bytes)
//   x0 = bytes -> x0 = region. This is the call the allocator tries first.
.global _BreakMarkJITMapping
_BreakMarkJITMapping:
    brk     #0x69
    ret

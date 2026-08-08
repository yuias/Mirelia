//! Allocation guard for the real-time thread.
//!
//! "The audio real-time thread never allocates" is a structural invariant
//! (kickoff §0); this makes violations impossible to miss in development:
//! a wrapper around the system allocator aborts the process if any
//! allocation or deallocation happens while the current thread is inside
//! the render callback. Release builds keep the wrapper but skip the check.
//!
//! The engine marks the RT section around each callback invocation; every
//! binary that links `mirelia-audio` (unit tests, the cdylib, and therefore
//! the conformance suite) inherits the guard automatically.

use std::alloc::{GlobalAlloc, Layout, System};
use std::cell::Cell;

thread_local! {
    // const-initialized: TLS access from inside the allocator must not
    // itself allocate.
    static IN_RT_SECTION: Cell<bool> = const { Cell::new(false) };
}

/// Marks the current thread as (not) being inside the RT render section.
pub fn set_rt_section(active: bool) {
    IN_RT_SECTION.with(|flag| flag.set(active));
}

#[cfg(debug_assertions)]
#[inline]
fn assert_outside_rt() {
    if IN_RT_SECTION.with(Cell::get) {
        // Cannot panic here: formatting the panic message would allocate
        // and re-enter this allocator. The aborting test/process names the
        // offending code path well enough.
        std::process::abort();
    }
}

#[cfg(not(debug_assertions))]
#[inline(always)]
fn assert_outside_rt() {}

/// System allocator wrapper enforcing the RT no-allocation rule.
pub struct RtGuardAllocator;

// SAFETY: pure delegation to `System` plus a side-effect-free check.
unsafe impl GlobalAlloc for RtGuardAllocator {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        assert_outside_rt();
        unsafe { System.alloc(layout) }
    }

    unsafe fn dealloc(&self, ptr: *mut u8, layout: Layout) {
        assert_outside_rt();
        unsafe { System.dealloc(ptr, layout) }
    }

    unsafe fn realloc(&self, ptr: *mut u8, layout: Layout, new_size: usize) -> *mut u8 {
        assert_outside_rt();
        unsafe { System.realloc(ptr, layout, new_size) }
    }

    unsafe fn alloc_zeroed(&self, layout: Layout) -> *mut u8 {
        assert_outside_rt();
        unsafe { System.alloc_zeroed(layout) }
    }
}

#[global_allocator]
static GLOBAL: RtGuardAllocator = RtGuardAllocator;

/// RAII marker for the RT section; clears the flag even on unwind.
pub struct RtSection;

impl RtSection {
    /// Enters the RT section on this thread.
    pub fn enter() -> Self {
        set_rt_section(true);
        RtSection
    }
}

impl Drop for RtSection {
    fn drop(&mut self) {
        set_rt_section(false);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn allocation_outside_rt_section_is_fine() {
        let before = Box::new(1);
        {
            let _rt = RtSection::enter();
            // No allocation here — allocating would abort the test process.
            let x = [0u8; 16];
            assert_eq!(x[0], 0);
        }

        // Flag cleared again: allocating must be legal after the section.
        let after = Box::new(2);
        assert_eq!(*before + *after, 3);
    }
}

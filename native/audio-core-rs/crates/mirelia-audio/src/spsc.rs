//! Wait-free single-producer single-consumer ring buffer.
//!
//! This is the only channel between the decode thread and the real-time
//! render thread. All consumer-side operations are allocation-free and
//! lock-free, which is what makes them legal on the RT path.

use std::cell::UnsafeCell;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};

/// Shared core of the ring. Indices are monotonically increasing sample
/// counters; slot = index % capacity. At realistic audio rates a usize
/// cannot wrap within the lifetime of a process.
pub struct SpscCore<T> {
    buf: Box<[UnsafeCell<T>]>,
    /// Total items ever popped. Written by the consumer only.
    read: AtomicUsize,
    /// Total items ever pushed. Written by the producer only.
    write: AtomicUsize,
}

// SAFETY: slots are only accessed by the producer between read+cap and write,
// and by the consumer between read and write; the atomics order the handoff.
unsafe impl<T: Send> Sync for SpscCore<T> {}

impl<T: Copy + Default> SpscCore<T> {
    /// Creates a core with the given capacity in items.
    pub fn new(capacity: usize) -> Arc<Self> {
        assert!(capacity > 0);
        Arc::new(Self {
            buf: (0..capacity).map(|_| UnsafeCell::new(T::default())).collect(),
            read: AtomicUsize::new(0),
            write: AtomicUsize::new(0),
        })
    }

    /// Capacity in items.
    pub fn capacity(&self) -> usize {
        self.buf.len()
    }

    /// Items currently queued.
    pub fn len(&self) -> usize {
        self.write.load(Ordering::Acquire) - self.read.load(Ordering::Acquire)
    }

    /// True when nothing is queued.
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }
}

/// Producer handle.
///
/// # Safety contract
///
/// At most one `Producer` and one `Consumer` may be live per core at any
/// time. `producer()`/`consumer()` are safe functions, so the engine is
/// responsible for handing them out sequentially, never concurrently.
pub struct Producer<T> {
    core: Arc<SpscCore<T>>,
}

/// Consumer handle. See the safety contract on [`Producer`].
pub struct Consumer<T> {
    core: Arc<SpscCore<T>>,
}

/// Creates a producer for the core. The caller must uphold the single-live-
/// producer contract documented on [`Producer`].
pub fn producer<T>(core: Arc<SpscCore<T>>) -> Producer<T> {
    Producer { core }
}

/// Creates a consumer for the core. The caller must uphold the single-live-
/// consumer contract documented on [`Producer`].
pub fn consumer<T>(core: Arc<SpscCore<T>>) -> Consumer<T> {
    Consumer { core }
}

impl<T: Copy + Default> Producer<T> {
    /// Free space in items.
    pub fn free(&self) -> usize {
        self.core.capacity() - self.core.len()
    }

    /// The producer's monotonic item counter (total items ever pushed).
    pub fn write_index(&self) -> usize {
        self.core.write.load(Ordering::Relaxed)
    }

    /// Pushes as many items as fit; returns how many were written.
    pub fn push_slice(&mut self, items: &[T]) -> usize {
        let core = &self.core;
        let cap = core.capacity();
        let read = core.read.load(Ordering::Acquire);
        let write = core.write.load(Ordering::Relaxed);
        let n = items.len().min(cap - (write - read));

        for (i, item) in items[..n].iter().enumerate() {
            let slot = (write + i) % cap;
            // SAFETY: slots in [write, read+cap) belong to the producer.
            unsafe { *core.buf[slot].get() = *item };
        }

        core.write.store(write + n, Ordering::Release);
        n
    }
}

impl<T: Copy + Default> Consumer<T> {
    /// Items currently queued.
    pub fn len(&self) -> usize {
        self.core.len()
    }

    /// True when nothing is queued.
    pub fn is_empty(&self) -> bool {
        self.core.is_empty()
    }

    /// The consumer's monotonic item counter (total items ever popped).
    pub fn read_index(&self) -> usize {
        self.core.read.load(Ordering::Relaxed)
    }

    /// Pops up to `out.len()` items; returns how many were written.
    pub fn pop_slice(&mut self, out: &mut [T]) -> usize {
        let core = &self.core;
        let cap = core.capacity();
        let write = core.write.load(Ordering::Acquire);
        let read = core.read.load(Ordering::Relaxed);
        let n = out.len().min(write - read);

        for (i, slot_out) in out[..n].iter_mut().enumerate() {
            let slot = (read + i) % cap;
            // SAFETY: slots in [read, write) belong to the consumer.
            *slot_out = unsafe { *core.buf[slot].get() };
        }

        core.read.store(read + n, Ordering::Release);
        n
    }

    /// Reads the oldest item without consuming it.
    pub fn peek(&self) -> Option<T> {
        let core = &self.core;
        let write = core.write.load(Ordering::Acquire);
        let read = core.read.load(Ordering::Relaxed);
        if write == read {
            return None;
        }

        // SAFETY: the slot at `read` belongs to the consumer while read < write.
        Some(unsafe { *core.buf[read % core.capacity()].get() })
    }

    /// Consumes one item.
    pub fn pop(&mut self) -> Option<T> {
        let mut out = [T::default()];
        (self.pop_slice(&mut out) == 1).then(|| out[0])
    }

    /// Discards everything currently queued.
    pub fn clear(&mut self) {
        let write = self.core.write.load(Ordering::Acquire);
        self.core.read.store(write, Ordering::Release);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn push_pop_roundtrip() {
        let core = SpscCore::<f32>::new(8);
        let mut p = producer(core.clone());
        let mut c = consumer(core);

        assert_eq!(p.push_slice(&[1.0, 2.0, 3.0]), 3);
        let mut out = [0.0f32; 2];
        assert_eq!(c.pop_slice(&mut out), 2);
        assert_eq!(out, [1.0, 2.0]);
        assert_eq!(c.pop(), Some(3.0));
        assert_eq!(c.pop(), None);
    }

    #[test]
    fn capacity_is_respected_and_wraps() {
        let core = SpscCore::<u32>::new(4);
        let mut p = producer(core.clone());
        let mut c = consumer(core);

        assert_eq!(p.push_slice(&[1, 2, 3, 4, 5]), 4);
        let mut out = [0u32; 3];
        assert_eq!(c.pop_slice(&mut out), 3);
        assert_eq!(p.push_slice(&[5, 6, 7]), 3);
        assert_eq!(c.pop(), Some(4));
        assert_eq!(c.pop(), Some(5));
        assert_eq!(c.pop(), Some(6));
        assert_eq!(c.pop(), Some(7));
    }

    #[test]
    fn clear_discards_queued_items()
    {
        let core = SpscCore::<u32>::new(4);
        let mut p = producer(core.clone());
        let mut c = consumer(core);
        p.push_slice(&[1, 2, 3]);
        c.clear();
        assert_eq!(c.pop(), None);
        assert_eq!(p.free(), 4);
    }

    #[test]
    fn threaded_transfer_preserves_order() {
        let core = SpscCore::<u32>::new(64);
        let mut p = producer(core.clone());
        let mut c = consumer(core);

        let writer = std::thread::spawn(move || {
            let mut next = 0u32;
            while next < 10_000 {
                let batch: Vec<u32> = (next..(next + 17).min(10_000)).collect();
                let pushed = p.push_slice(&batch);
                next += pushed as u32;
                if pushed == 0 {
                    std::thread::yield_now();
                }
            }
        });

        let mut expected = 0u32;
        while expected < 10_000 {
            if let Some(v) = c.pop() {
                assert_eq!(v, expected);
                expected += 1;
            } else {
                std::thread::yield_now();
            }
        }

        writer.join().unwrap();
    }
}

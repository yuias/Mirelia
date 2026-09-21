# ADR 0007: Avalonia.Controls.DataGrid instead of TreeDataGrid

- Status: accepted
- Date: 2026-08-08
- Amends: ADR 0001 (which assumed TreeDataGrid), kickoff §2.2 / open question 2

## Context

The kickoff planned around `Avalonia.Controls.TreeDataGrid` as "the one
control we depend on heavily". Discovered while building the track grid:
**TreeDataGrid 12.x requires a commercial AvaloniaUI license**
(`AvaloniaUI.Licensing`, build fails with AVLIC0001 without a key). The last
MIT versions target Avalonia 11.x only.

## Options considered

1. Pin Avalonia 11.3 + MIT TreeDataGrid 11.x — stays free, but locks the
   whole app to the old major, and every future TreeDataGrid version is
   commercial anyway, so the lock has no exit.
2. Buy/obtain an AvaloniaUI license — recurring cost and a portal-issued
   key baked into the build for an open-stack hobby-grade dependency.
3. **Avalonia 12 + `Avalonia.Controls.DataGrid` (MIT, ships from the
   Avalonia repo, version-matched)** — chosen.

## Decision

Use `Avalonia.Controls.DataGrid` with row virtualization over the
ID-array-plus-page-cache list from kickoff §4.3.

Measured caveat: DataGrid's collection view **snapshots the source list at
load** (a full enumeration — the first smoke run hydrated all 392 pages of
a 100k library). The §4.3 design is therefore adapted: placeholder stubs
exist for every row (cheap, flat objects), but row *content* hydrates
page-wise only when rows become visible (`DataGrid.LoadingRow` →
`VirtualTrackList.NotifyVisible`) and is LRU-evicted back to placeholders.
After the change the same smoke run hydrates exactly 1 page.

Notes that make this workable:

- The virtualization design lives in `VirtualTrackList`, not in the grid
  control; swapping grids touched only the XAML and the source property.
- DataGrid's `Sorting` hook can be intercepted, which is exactly what §7.2
  needs (column sorting must go through the database):
  the handler marks the event `Handled` (suppressing the collection view's
  in-memory sort) and re-queries SQL with the column's ORDER BY mapping.
- Grouping-by-album (§7.2: group headers as ordinary rows with a sentinel)
  was already planned outside the grid's own grouping API, so losing
  TreeDataGrid's hierarchy support costs nothing.

## Revisit when

100k-row scroll measurements show DataGrid's realization overhead is the
bottleneck — the fallback is a hand-rolled virtualizing panel, not a return
to TreeDataGrid.

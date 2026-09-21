# ADR 0003: SQLite via Microsoft.Data.Sqlite (raw ADO.NET, not EF Core)

- Status: accepted
- Date: 2026-08-08

## Decision

Library storage is SQLite accessed through `Microsoft.Data.Sqlite` with
hand-written SQL. EF Core is explicitly rejected.

## Rationale

- EF Core's SQL generation is opaque at exactly the point we care most
  about: smart-playlist evaluation. We use expression trees *as an AST* and
  compile them to SQL ourselves.
- EF Core's change tracker is the wrong shape for a mostly-read, bulk-write
  workload (100k-track scans).
- Sorting/filtering always happens in SQL; the UI holds only ID arrays.

## Open-time pragmas

```
journal_mode = WAL
synchronous  = NORMAL
foreign_keys = ON
temp_store   = MEMORY
mmap_size    = 268435456
```

## Dependencies introduced

| Package | Replaces | Why |
|---|---|---|
| `Microsoft.Data.Sqlite` | EF Core | Thin ADO.NET provider, bundled native SQLite |
| `TagLibSharp` | hand-written tag parsers | Tag read/write for all v1 formats incl. sort tags |

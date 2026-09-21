# ADR 0001: C# / .NET + Avalonia for the managed application stack

- Status: accepted
- Date: 2026-08-08

## Decision

The application layer (UI, library management, query engine, sync server) is
C# 14 on .NET 10 (LTS), with Avalonia 12.x as the UI framework.

## Alternatives considered

Rust + Tauri was the main alternative and was rejected.

## Rationale

- LINQ expression trees give us a ready-made AST with visitor infrastructure
  for the smart-playlist engine. GUI builder → expression tree → SQL and
  text DSL → expression tree → SQL converge for free. In Rust this is weeks
  of bespoke AST + compiler work.
- No IPC serialization boundary. A library manager pushes large,
  frequently-resorted datasets into a virtualized grid; Tauri requires
  careful paging across a JSON IPC hop to stay fast.
- Avalonia renders through Skia on all OSes — one rendering target to tune,
  not two (Chromium on Windows, WKWebView on macOS).
- TagLib# covers all v1 tag formats including sort-order tags.

## Accepted costs

- GC pauses: mitigated by keeping audio entirely off the CLR (ADR 0002) and
  keeping only track IDs resident in the UI layer.
- Avalonia is younger than WPF: `TreeDataGrid` is the one control we depend
  on heavily; budget time to read its source.

## Dependencies introduced

| Package | Replaces | Why |
|---|---|---|
| `Avalonia` 12.x | WPF / Tauri webview | Cross-platform Skia UI, see above |
| `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Diagnostics` | — | Standard Avalonia app companions |
| `Avalonia.Controls.TreeDataGrid` | hand-rolled virtualizing grid | Purpose-built virtualized grid (added with the track grid; listed here because the stack decision depends on it) |

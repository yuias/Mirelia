# ADR 0006: CommunityToolkit.Mvvm for view-model plumbing

- Status: accepted
- Date: 2026-08-08

## Decision

`Mirelia.App` uses `CommunityToolkit.Mvvm` (source-generated
`ObservableObject` / `RelayCommand`) for view-model boilerplate.

## Rationale

- Hand-rolled `INotifyPropertyChanged` is the alternative and it is pure
  noise; the toolkit's source generators produce the same code without the
  transcription errors.
- No runtime framework lock-in: generated code is plain INPC, bindings stay
  standard Avalonia. Removing the package later is mechanical.
- Deliberately **not** adopted: its messenger and IoC helpers. Composition
  stays explicit in `App.axaml.cs`.

## Dependencies introduced

| Package | Replaces | Why |
|---|---|---|
| `CommunityToolkit.Mvvm` | hand-written INPC | Source-generated observable properties and commands |

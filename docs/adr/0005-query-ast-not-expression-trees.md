# ADR 0005: Hand-written query AST instead of LINQ expression trees

- Status: accepted
- Date: 2026-08-08
- Resolves: kickoff open question 3

## Decision

`Mirelia.Query` models parsed queries as a small hand-written record AST
(`ParsedQuery`, `QueryExpr`, `QueryValue`), not as
`Expression<Func<Track, bool>>`.

## Context

The kickoff argued for expression trees because they provide a ready-made
AST with visitor infrastructure, and left the final call open until end of
M1. Implementing M1 settled it:

- We never compile to IL. Both consumers — the SQL compiler and the DSL
  printer — are plain tree walks; `ExpressionVisitor` buys nothing over a
  `switch` on sealed records.
- Expression trees cannot represent the DSL's own constructs (`any()`,
  `playlist =`, `LIMIT n BY field`, relative dates) without smuggling them
  through marker method calls, which is exactly the visitor-fighting the
  kickoff warned about.
- The record AST is ~100 lines, exhaustively matchable, and trivially
  constructed by the future GUI condition builder (M6) — records with value
  equality also make round-trip tests cheap.

## Consequences

- The GUI builder and the text editor still converge on one model; only the
  model is ours instead of `System.Linq.Expressions`.
- The canonical persisted form remains the DSL text
  (`playlist.query_text`); nothing downstream changes.

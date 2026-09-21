# Development rules

## Before you finish a task

- `dotnet build -warnaserror` and `cargo clippy -- -D warnings` both clean.
- If you touched the FFI, run `native/tests/abi-conformance`.
- If you touched query compilation, run the DSL round-trip test suite.

## Comment style

- Write concise code comments
  - Focus on the "why" rather than the "what".
  - Avoid stating the obvious; only explain non-trivial logic or edge cases.
- Keep comments up to date when changing the code they describe.
- Do not add comments that merely mark generated or modified sections.

### C#

- Use single-line comments (`//`) for brief explanations.
- Avoid multi-line comments (`/* */`) for longer explanations.
- For describing methods, classes, fields, and all public members use XML comments.
- Place the comment on a separate line, not at the end of a line of code.
- Begin comment text with an uppercase letter.
- End comment text with a period.
- Insert one space between the comment delimiter (`//`) and the comment text, as shown in the following example.

### C/C++

- Prefer `//` over `/* */`. Both are acceptable, but be consistent within a file.

### Rust

- Document public APIs with doc comments (/// in Rust)

## C# Naming conventions

.NET C# identifier naming rules and conversions (https://raw.githubusercontent.com/dotnet/docs/refs/heads/main/docs/csharp/fundamentals/coding-style/identifier-names.md).

## C/C++ Naming conventions

Google C++ Style Guide (https://google.github.io/styleguide/cppguide.html) with the following modifications.

| Kind | Convention | Example |
| --- | --- | --- |
| Variables (incl. arguments) | `snake_case` | `file_path` |
| Local variables | `snake_case` preferred; `camelCase` acceptable | `file_path` |
| Functions | `camelCase` | `openFile()` |
| Types (class, struct, enum, enum class, typedef, using) | `PascalCase` | `FileReader` |
| Concepts | `PascalCase` | `Readable` |
| Class data members | `snake_case` + trailing underscore | `file_path_` |
| Struct data members | `snake_case` | `file_path` |
| Constants (`constexpr`, etc.) | `k` + `PascalCase` | `kFilePath` |
| Enumerators | `PascalCase` | `NotFound` |
| Macros (discouraged) | `ALL_CAPS_WITH_UNDERSCORES` | `LOG_ERROR` |

### Consistency

- Local variable style must be consistent within a function and, preferably, within a file. Match the surrounding code.

### Exceptions

- Deviate when aligning with STL or external library conventions,
  e.g. `begin()`, `end()`, `value_type`, `iterator_category`.
- Types intended to satisfy STL concepts follow STL naming.

# Development rules

## Before you finish a task

- `dotnet build -warnaserror` and `cargo clippy -- -D warnings` both clean.
- If you touched the FFI, run `native/tests/abi-conformance`.
- If you touched query compilation, run the DSL round-trip test suite.

## Building C and C++ on Windows

clang needs the Windows SDK and MSVC headers and libraries from a Visual Studio
developer environment. Run this in PowerShell before CMake or Ninja:

```powershell
# The Windows SDK environment comes from the VS DevShell; skip if already inside one.
if (-not $env:VSCMD_VER) {
    if (-not (Get-Module -ListAvailable -Name VSSetup)) {
        throw "Install-Module VSSetup -Scope CurrentUser"
    }
    Import-Module VSSetup
    $vsInstance = Get-VSSetupInstance | Select-VSSetupInstance -Version '[18.0,19.0)'
    if ($null -eq $vsInstance) {
        throw "Visual Studio 18.x (VS 2026) with the Windows SDK was not found."
    }
    Import-Module (Join-Path $vsInstance.InstallationPath "Common7/Tools/Microsoft.VisualStudio.DevShell.dll")
    Enter-VsDevShell $vsInstance.InstanceId -SkipAutomaticLocation -DevCmdArguments "-arch=x64 -host_arch=x64"
}
```

## Comment style

- Write code comments in English. Explain why, not what; skip comments on
  obvious code; update comments when you change the code they describe; do not
  add comments that only mark generated or modified sections. A language's own
  conventions take precedence.
- State things literally. Do not replace a plain phrase with a metaphor or
  flourish. Figurative wording adds connotations you did not choose and makes
  the reader decode it.

### Additional style for C#

- Use single-line comments (`//`) for brief explanations.
- Avoid multi-line comments (`/* */`) for longer explanations.
- For describing methods, classes, fields, and all public members use XML comments.
- Place the comment on a separate line, not at the end of a line of code.
- Begin comment text with an uppercase letter.
- End comment text with a period.
- Insert one space between the comment delimiter (`//`) and the comment text, as shown in the following example.

### Additional style for C/C++

- Prefer `//` over `/* */`. Both are acceptable, but be consistent within a file.

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

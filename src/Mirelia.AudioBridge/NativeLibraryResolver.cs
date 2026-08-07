using System.Reflection;
using System.Runtime.InteropServices;

namespace Mirelia.AudioBridge;

/// <summary>
/// Resolves the native audio backend library. Default is the Rust backend;
/// a C++ backend can be substituted later via the same mechanism
/// (see docs/audio-abi.md, "Backend selection").
/// </summary>
internal static class NativeLibraryResolver
{
    /// <summary>
    /// Environment variable holding an explicit full path to the backend
    /// library. Takes precedence over all probing.
    /// </summary>
    private const string OverrideVariable = "MIRELIA_AUDIO_CORE";

    private static bool _registered;

    /// <summary>Installs the resolver for <paramref name="assembly"/>. Idempotent.</summary>
    internal static void Register(Assembly assembly)
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        NativeLibrary.SetDllImportResolver(assembly, Resolve);
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != NativeMethods.LibraryName)
        {
            return nint.Zero;
        }

        string? overridePath = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrEmpty(overridePath) && NativeLibrary.TryLoad(overridePath, out nint fromOverride))
        {
            return fromOverride;
        }

        // Normal deployment: the library sits next to the executable.
        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out nint fromDefault))
        {
            return fromDefault;
        }

        // Development: probe the cargo target directory upward from the app base,
        // so `dotnet run` works without a copy step after `cargo build`.
        string fileName = PlatformFileName(libraryName);
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (string profile in (string[])["debug", "release"])
            {
                string candidate = Path.Combine(
                    dir.FullName, "native", "audio-core-rs", "target", profile, fileName);
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out nint fromDev))
                {
                    return fromDev;
                }
            }
        }

        return nint.Zero;
    }

    private static string PlatformFileName(string libraryName)
    {
        if (OperatingSystem.IsWindows())
        {
            return libraryName + ".dll";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "lib" + libraryName + ".dylib";
        }

        return "lib" + libraryName + ".so";
    }
}

#!/usr/bin/env dotnet run
// Builds a release of Mirelia for the host platform and zips it up.
//
//   dotnet run scripts/package.cs -- [--output <dir>] [--skip-native]
//
// Produces artifacts/Mirelia-<version>-<githash>.zip containing the desktop
// app, the CLI, and the native audio backend. On Windows it also contains the
// r8brain resampler plugin under plugins/dsp/. Framework-dependent: the target
// machine needs the .NET runtime.
//
// Without --skip-native, a Windows run also builds the plugin with clang and
// Ninja. That needs the Visual Studio developer environment (see "Building C
// and C++ on Windows" in AGENTS.md) and network access, because CMake fetches
// the r8brain source when it configures.

using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;

string? outputOption = null;
bool skipNative = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--output" when i + 1 < args.Length:
            outputOption = args[++i];
            break;
        case "--skip-native":
            skipNative = true;
            break;
        default:
            Console.Error.WriteLine($"package: unknown argument: {args[i]}");
            return 2;
    }
}

string rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
string root = FindRepoRoot();
string version = ReadVersion(Path.Combine(root, "Directory.Build.props"));
string? gitHash = ReadGitHash(root);
string stem = gitHash is null ? $"Mirelia-{version}" : $"Mirelia-{version}-{gitHash}";

string outputDir = Path.GetFullPath(outputOption ?? Path.Combine(root, "artifacts"));
string staging = Path.Combine(outputDir, stem);
string zipPath = Path.Combine(outputDir, stem + ".zip");

// Fail before the long cargo and publish steps rather than after them if the
// compiler environment for the plugin is missing.
if (!skipNative && OperatingSystem.IsWindows()
    && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VSCMD_VER")))
{
    Console.Error.WriteLine(
        "package: building the r8brain plugin needs the Visual Studio developer environment. "
        + "See \"Building C and C++ on Windows\" in AGENTS.md.");
    return 1;
}

Console.WriteLine($"packaging {stem} for {rid}");

// A stale staging directory would smuggle files from an older build into the zip.
if (Directory.Exists(staging))
{
    Directory.Delete(staging, recursive: true);
}

Directory.CreateDirectory(staging);

if (!skipNative)
{
    Run(root, "cargo", "build", "--release", "--manifest-path", "native/audio-core-rs/Cargo.toml");

    // The plugin is a separate CMake project; configuring it fetches r8brain.
    if (OperatingSystem.IsWindows())
    {
        Run(root, "cmake", "-S", "native/plugins/r8brain-resampler", "-B", "build/r8brain-resampler",
            "-G", "Ninja", "-DCMAKE_CXX_COMPILER=clang++", "-DCMAKE_BUILD_TYPE=Release");
        Run(root, "cmake", "--build", "build/r8brain-resampler");
    }
}

// Pinned to the host RID: a portable publish would ship Skia and HarfBuzz for
// every runtime Avalonia supports, which is most of a 126 MB archive.
//
// ReadyToRun costs ~16 MB of archive and buys a third off startup (0.99s ->
// 0.63s) and 27 MB off the idle working set (154 MB -> 128 MB), because the
// framework and Avalonia no longer have to be JITted on the way up. It needs
// the RID, which is why it lives here rather than in the project file.
// Trimming is deliberately not enabled: Avalonia resolves plenty by
// reflection, and the failures that causes surface at runtime as missing
// controls, which a smoke run would not necessarily catch.
foreach (string project in (string[])["src/Mirelia.App", "src/Mirelia.Cli"])
{
    Run(root, "dotnet", "publish", project, "-c", "Release",
        "-r", rid, "--self-contained", "false",
        "-p:PublishReadyToRun=true", "-o", staging);
}

// The resolver looks for the backend next to the executable; publish does not
// know about the cargo output, so copy it in.
string nativeName = OperatingSystem.IsWindows() ? "audio_core_rs.dll"
    : OperatingSystem.IsMacOS() ? "libaudio_core_rs.dylib"
    : "libaudio_core_rs.so";
string nativeSource = Path.Combine(root, "native", "audio-core-rs", "target", "release", nativeName);
if (!File.Exists(nativeSource))
{
    Console.Error.WriteLine($"package: native backend not built: {nativeSource}");
    return 1;
}

File.Copy(nativeSource, Path.Combine(staging, nativeName), overwrite: true);

// The app scans plugins/dsp next to the executable. The plugin is not ported to
// macOS, so only the Windows archive carries one.
if (OperatingSystem.IsWindows())
{
    string pluginSource = Path.Combine(root, "build", "r8brain-resampler", "mirelia_r8brain.dll");
    if (!File.Exists(pluginSource))
    {
        Console.Error.WriteLine($"package: r8brain plugin not built: {pluginSource}");
        return 1;
    }

    string pluginStaging = Path.Combine(staging, "plugins", "dsp");
    Directory.CreateDirectory(pluginStaging);
    File.Copy(pluginSource, Path.Combine(pluginStaging, "mirelia_r8brain.dll"), overwrite: true);
}

// The MIT text and the notices the LGPL and MPL components require have to
// travel with the binaries, not just live in the repository.
foreach (string doc in (string[])["LICENSE", "THIRD-PARTY-NOTICES.md", "README.md"])
{
    File.Copy(Path.Combine(root, doc), Path.Combine(staging, doc), overwrite: true);
}

// The notices file only names the licenses; the LGPL and MPL both want the
// text itself to travel with the binaries.
string licenseStaging = Path.Combine(staging, "licenses");
Directory.CreateDirectory(licenseStaging);
foreach (string text in Directory.GetFiles(Path.Combine(root, "licenses")))
{
    File.Copy(text, Path.Combine(licenseStaging, Path.GetFileName(text)), overwrite: true);
}

// Debug symbols roughly double the archive and help nobody downstream.
foreach (string pdb in Directory.GetFiles(staging, "*.pdb", SearchOption.AllDirectories))
{
    File.Delete(pdb);
}

if (File.Exists(zipPath))
{
    File.Delete(zipPath);
}

ZipFile.CreateFromDirectory(staging, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
Directory.Delete(staging, recursive: true);

Console.WriteLine($"wrote {zipPath} ({new FileInfo(zipPath).Length / (1024.0 * 1024.0):F1} MB)");
return 0;

static string FindRepoRoot()
{
    for (DirectoryInfo? dir = new(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Mirelia.slnx")))
        {
            return dir.FullName;
        }
    }

    throw new InvalidOperationException("Run this from inside the Mirelia checkout.");
}

static string ReadVersion(string propsPath)
{
    string? version = XDocument.Load(propsPath)
        .Descendants("Version")
        .FirstOrDefault()?.Value.Trim();
    return string.IsNullOrEmpty(version)
        ? throw new InvalidOperationException($"No <Version> in {propsPath}.")
        : version;
}

// Best effort, and the length matches AppInfo.HashLength so the archive name
// and what the app reports about itself line up.
static string? ReadGitHash(string root)
{
    try
    {
        using Process? git = Process.Start(new ProcessStartInfo("git")
        {
            ArgumentList = { "rev-parse", "--short=10", "HEAD" },
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        if (git is null)
        {
            return null;
        }

        string hash = git.StandardOutput.ReadToEnd().Trim();
        git.WaitForExit();
        return git.ExitCode == 0 && hash.Length > 0 ? hash : null;
    }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
        return null;
    }
}

static void Run(string workingDirectory, string file, params string[] arguments)
{
    Console.WriteLine($"  $ {file} {string.Join(' ', arguments)}");
    var info = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory };
    foreach (string argument in arguments)
    {
        info.ArgumentList.Add(argument);
    }

    using Process process = Process.Start(info)
        ?? throw new InvalidOperationException($"Could not start {file}.");
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"{file} exited with {process.ExitCode}.");
    }
}

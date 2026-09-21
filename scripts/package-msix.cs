#!/usr/bin/env dotnet run
#:package SkiaSharp
// Builds a release of Mirelia and packs it into an MSIX for the Microsoft Store.
//
//   dotnet run scripts/package-msix.cs -- [options]
//
// Produces artifacts/Mirelia-<version>.msix. The package is deliberately left
// unsigned: the Store re-signs submissions on its side. To install one locally
// for testing, sign it with signtool and a certificate the machine trusts.
//
// Unlike scripts/package.cs this publishes self-contained. Nothing on the Store
// side supplies the .NET runtime to a packaged desktop app, so the runtime has
// to travel inside the package.

using System.Diagnostics;
using System.Xml.Linq;
using SkiaSharp;

// Deliberately not a real identity. The Store issues all three when the app
// name is reserved in Partner Center, and a package carrying these can never be
// submitted, so the script refuses to build one unless asked twice.
const string PlaceholderPackageName = "Publisher.Mirelia";
const string PlaceholderPublisher = "CN=00000000-0000-0000-0000-000000000000";
const string PlaceholderPublisherDisplayName = "Publisher Display Name";

string? outputOption = null;
string? versionOption = null;
string packageName = PlaceholderPackageName;
string publisher = PlaceholderPublisher;
string publisherDisplayName = PlaceholderPublisherDisplayName;
bool skipNative = false;
bool allowPlaceholderIdentity = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--output" when i + 1 < args.Length:
            outputOption = args[++i];
            break;
        case "--version" when i + 1 < args.Length:
            versionOption = args[++i];
            break;
        case "--package-name" when i + 1 < args.Length:
            packageName = args[++i];
            break;
        case "--publisher" when i + 1 < args.Length:
            publisher = args[++i];
            break;
        case "--publisher-display-name" when i + 1 < args.Length:
            publisherDisplayName = args[++i];
            break;
        case "--skip-native":
            skipNative = true;
            break;
        case "--allow-placeholder-identity":
            allowPlaceholderIdentity = true;
            break;
        default:
            Console.Error.WriteLine($"package-msix: unknown argument: {args[i]}");
            return 2;
    }
}

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("package-msix: MSIX packaging is Windows only.");
    return 1;
}

bool placeholderIdentity = packageName == PlaceholderPackageName
    || publisher == PlaceholderPublisher
    || publisherDisplayName == PlaceholderPublisherDisplayName;

if (placeholderIdentity && !allowPlaceholderIdentity)
{
    Console.Error.WriteLine(
        """
        package-msix: the package identity is still the placeholder.

        Pass the values Partner Center issued for this app:

          --package-name           <Identity Name>
          --publisher              <Identity Publisher, e.g. CN=00000000-...>
          --publisher-display-name <PublisherDisplayName>

        To build an unsubmittable package for local testing instead, pass
        --allow-placeholder-identity.
        """);
    return 2;
}

string rid = "win-x64";
string root = FindRepoRoot();
string version = ToFourPartVersion(versionOption ?? ReadVersion(Path.Combine(root, "Directory.Build.props")));
string stem = placeholderIdentity ? $"Mirelia-{version}-placeholder-identity" : $"Mirelia-{version}";

string outputDir = Path.GetFullPath(outputOption ?? Path.Combine(root, "artifacts"));
string staging = Path.Combine(outputDir, stem + ".msix-staging");
string msixPath = Path.Combine(outputDir, stem + ".msix");
string iconSource = Path.Combine(root, "resources", "icons", "Mirelia-icon-master-1254.png");
string manifestTemplate = Path.Combine(root, "packaging", "msix", "AppxManifest.xml");

// Fail before the publish rather than after it if the SDK is missing.
string makeAppx = FindMakeAppx();

Console.WriteLine($"packaging {stem} for {rid}");

// A stale staging directory would smuggle files from an older build into the package.
if (Directory.Exists(staging))
{
    Directory.Delete(staging, recursive: true);
}

Directory.CreateDirectory(staging);

if (!skipNative)
{
    Run(root, "cargo", "build", "--release", "--manifest-path", "native/audio-core-rs/Cargo.toml");
}

// ReadyToRun for the same reason as the zip build; trimming stays off because
// Avalonia resolves plenty by reflection.
foreach (string project in (string[])["src/Mirelia.App", "src/Mirelia.Cli"])
{
    Run(root, "dotnet", "publish", project, "-c", "Release",
        "-r", rid, "--self-contained", "true",
        "-p:PublishReadyToRun=true", "-o", staging);
}

// The resolver looks for the backend next to the executable; publish does not
// know about the cargo output, so copy it in.
string nativeSource = Path.Combine(root, "native", "audio-core-rs", "target", "release", "audio_core_rs.dll");
if (!File.Exists(nativeSource))
{
    Console.Error.WriteLine($"package-msix: native backend not built: {nativeSource}");
    return 1;
}

File.Copy(nativeSource, Path.Combine(staging, "audio_core_rs.dll"), overwrite: true);

// The MIT text and the notices the LGPL and MPL components require have to
// travel with the binaries, not just live in the repository.
foreach (string doc in (string[])["LICENSE", "THIRD-PARTY-NOTICES.md"])
{
    File.Copy(Path.Combine(root, doc), Path.Combine(staging, doc), overwrite: true);
}

// Debug symbols roughly double the package and help nobody downstream.
foreach (string pdb in Directory.GetFiles(staging, "*.pdb", SearchOption.AllDirectories))
{
    File.Delete(pdb);
}

WriteAssets(iconSource, Path.Combine(staging, "Assets"));

ExpandManifest(manifestTemplate, Path.Combine(staging, "AppxManifest.xml"), new()
{
    ["PACKAGE_NAME"] = packageName,
    ["PUBLISHER"] = publisher,
    ["PUBLISHER_DISPLAY_NAME"] = publisherDisplayName,
    ["VERSION"] = version,
});

if (File.Exists(msixPath))
{
    File.Delete(msixPath);
}

Run(root, makeAppx, "pack", "/d", staging, "/p", msixPath, "/o");
Directory.Delete(staging, recursive: true);

Console.WriteLine($"wrote {msixPath} ({new FileInfo(msixPath).Length / (1024.0 * 1024.0):F1} MB)");
Console.WriteLine("the package is unsigned; sign it with signtool to install it locally");
if (placeholderIdentity)
{
    Console.WriteLine("the identity is the placeholder: this package cannot be submitted to the Store");
}

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

// The Store wants four parts and reserves the revision for itself, so a
// three-part project version grows a trailing zero rather than being rejected
// on submission.
static string ToFourPartVersion(string value)
{
    string[] parts = value.Split('.');
    return parts.Length switch
    {
        3 => $"{value}.0",
        4 when parts[3] == "0" => value,
        4 => throw new InvalidOperationException($"The Store requires the revision to be 0, got: {value}"),
        _ => throw new InvalidOperationException($"Version must be 3 or 4 dotted parts, got: {value}"),
    };
}

static string FindMakeAppx()
{
    string sdkRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "bin");
    if (!Directory.Exists(sdkRoot))
    {
        throw new InvalidOperationException($"Windows SDK not found at {sdkRoot}. Install the Windows 10/11 SDK.");
    }

    string? found = Directory.GetDirectories(sdkRoot)
        .Select(Path.GetFileName)
        .Where(name => Version.TryParse(name, out _))
        .OrderByDescending(name => Version.Parse(name!))
        .Select(name => Path.Combine(sdkRoot, name!, "x64", "makeappx.exe"))
        .FirstOrDefault(File.Exists);

    return found ?? throw new InvalidOperationException(
        $"makeappx.exe not found under {sdkRoot}. Install the Windows 10/11 SDK.");
}

// Sizes follow the Microsoft Store guidance for packaged desktop apps. The
// source icon is square, so the wide tile and the splash screen get it centred
// on transparency rather than stretched.
static void WriteAssets(string iconSource, string assetsDir)
{
    (string Name, int Width, int Height)[] spec =
    [
        ("Square44x44Logo.png", 44, 44),
        ("Square150x150Logo.png", 150, 150),
        ("SmallTile.png", 71, 71),
        ("LargeTile.png", 310, 310),
        ("Wide310x150Logo.png", 310, 150),
        ("StoreLogo.png", 50, 50),
        ("SplashScreen.png", 620, 300),
    ];

    Directory.CreateDirectory(assetsDir);

    using SKBitmap decoded = SKBitmap.Decode(iconSource)
        ?? throw new InvalidOperationException($"Could not decode {iconSource}.");
    using SKImage source = SKImage.FromBitmap(decoded);

    foreach ((string name, int width, int height) in spec)
    {
        using SKBitmap target = new(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (SKCanvas canvas = new(target))
        {
            canvas.Clear(SKColors.Transparent);
            int side = Math.Min(width, height);
            SKRect destination = SKRect.Create((width - side) / 2f, (height - side) / 2f, side, side);
            canvas.DrawImage(source, destination, new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        using SKData encoded = target.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream file = File.Create(Path.Combine(assetsDir, name));
        encoded.SaveTo(file);
    }
}

static void ExpandManifest(string templatePath, string destinationPath, Dictionary<string, string> replacements)
{
    string content = File.ReadAllText(templatePath);
    foreach ((string key, string value) in replacements)
    {
        content = content.Replace($"{{{{{key}}}}}", value);
    }

    // Catch a typo in a token name here rather than in makeappx's output.
    int unresolved = content.IndexOf("{{", StringComparison.Ordinal);
    if (unresolved >= 0)
    {
        int end = Math.Min(content.Length, unresolved + 40);
        throw new InvalidOperationException(
            $"Unresolved placeholder in {templatePath}: {content[unresolved..end]}");
    }

    File.WriteAllText(destinationPath, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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

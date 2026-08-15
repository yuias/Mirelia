using System.Reflection;

namespace Mirelia.Library;

/// <summary>
/// Product identity shared by the app and the CLI, so the About dialog and
/// <c>--version</c> never drift apart.
/// </summary>
public static class AppInfo
{
    /// <summary>Product name as shown to users.</summary>
    public const string Name = "Mirelia";

    /// <summary>Release version, without build metadata.</summary>
    public static string Version { get; }

    /// <summary>
    /// Abbreviated commit this build came from, or null when the build had no
    /// readable git checkout.
    /// </summary>
    public static string? GitHash { get; }

    /// <summary>How much of the commit id is shown. Matches scripts/package.cs.</summary>
    private const int HashLength = 10;

    static AppInfo()
    {
        Assembly assembly = typeof(AppInfo).Assembly;
        string informational =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        // The SDK stamps the full commit as semver build metadata when the build
        // ran from a checkout: "0.1.0+7bc48ede5831bfe2eed1a9f5ad063b3b7f9afb7d".
        int plus = informational.IndexOf('+');
        Version = plus < 0 ? informational : informational[..plus];
        string hash = plus < 0 ? string.Empty : informational[(plus + 1)..];
        GitHash = hash.Length == 0
            ? null
            : hash[..Math.Min(HashLength, hash.Length)];
    }

    /// <summary>One-line form: <c>Mirelia 0.1.0 (abc1234)</c>.</summary>
    public static string VersionLine =>
        GitHash is null ? $"{Name} {Version}" : $"{Name} {Version} ({GitHash})";
}

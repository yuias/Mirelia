namespace Mirelia.Library;

/// <summary>Well-known locations shared by the app and the CLI.</summary>
public static class LibraryPaths
{
    /// <summary>
    /// The MIRELIA_DB override, or null when unset. Tests and the UI smoke
    /// run set it, and it outranks the library the app last had open.
    /// </summary>
    public static string? DatabaseOverride =>
        Environment.GetEnvironmentVariable("MIRELIA_DB");

    /// <summary>
    /// The default library database path. Overridable with the MIRELIA_DB
    /// environment variable (used by tests and the UI smoke run).
    /// </summary>
    public static string DefaultDatabasePath =>
        DatabaseOverride
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Mirelia",
            "library.db");

    /// <summary>
    /// Where the rolling application log lives. Anchored to the default
    /// library's folder for the same reason settings are: a bug report must
    /// not depend on which library happened to be open.
    /// </summary>
    public static string LogFilePath => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(DefaultDatabasePath))!,
        "mirelia.log");

    /// <summary>
    /// Where app-level settings live. Anchored to the default library's
    /// folder, not to whichever library happens to be open, so preferences
    /// like theme and output device do not fork per library.
    /// </summary>
    public static string SettingsFilePath => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(DefaultDatabasePath))!,
        "settings.json");
}

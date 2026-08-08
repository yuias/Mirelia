namespace Mirelia.Library;

/// <summary>Well-known locations shared by the app and the CLI.</summary>
public static class LibraryPaths
{
    /// <summary>
    /// The default library database path. Overridable with the MIRELIA_DB
    /// environment variable (used by tests and the UI smoke run).
    /// </summary>
    public static string DefaultDatabasePath =>
        Environment.GetEnvironmentVariable("MIRELIA_DB")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Mirelia",
            "library.db");
}

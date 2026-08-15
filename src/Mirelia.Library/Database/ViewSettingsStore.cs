using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Mirelia.Library.Querying;

namespace Mirelia.Library.Database;

/// <summary>
/// Per-view UI preferences: which columns show (and their order) and how
/// the view sorts. Null members mean "use the view's default".
/// </summary>
public sealed record ViewSettings
{
    /// <summary>Column keys in display order, or null for the default set.</summary>
    public List<string>? Columns { get; init; }

    /// <summary>Sort preset, or null for the view's default.</summary>
    public TrackSortOrder? Sort { get; init; }

    /// <summary>
    /// Column key of a header-click sort overriding <see cref="Sort"/>,
    /// or null when no column sort is active.
    /// </summary>
    public string? SortColumn { get; init; }

    /// <summary>Direction of the column sort; meaningless when <see cref="SortColumn"/> is null.</summary>
    public bool SortDescending { get; init; }

    /// <summary>
    /// User-resized pixel widths, keyed by column key. Missing keys fall back
    /// to the registry default; null means no column was ever resized.
    /// </summary>
    public Dictionary<string, double>? ColumnWidths { get; init; }
}

/// <summary>
/// Persists <see cref="ViewSettings"/> per scope key (e.g. "source:music",
/// "playlist:42") in the <c>view_settings</c> table (schema v2).
/// </summary>
public sealed class ViewSettingsStore(LibraryDatabase db)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Settings for a scope; defaults when absent or unreadable.</summary>
    public ViewSettings Get(string scopeKey)
    {
        if (db.Scalar(
                "SELECT json FROM view_settings WHERE scope_key = $k",
                ("$k", scopeKey)) is not string json)
        {
            return new ViewSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<ViewSettings>(json, Options) ?? new ViewSettings();
        }
        catch (JsonException)
        {
            return new ViewSettings();
        }
    }

    /// <summary>Stores settings for a scope.</summary>
    public void Set(string scopeKey, ViewSettings settings)
    {
        db.Execute("""
            INSERT INTO view_settings (scope_key, json) VALUES ($k, $j)
            ON CONFLICT(scope_key) DO UPDATE SET json = excluded.json
            """,
            ("$k", scopeKey),
            ("$j", JsonSerializer.Serialize(settings, Options)));
    }

    /// <summary>
    /// Drops the saved column layout and widths of every scope, so all views
    /// fall back to the defaults. Sort state is deliberately left alone:
    /// this is a columns reset, not a view reset.
    /// </summary>
    public void ClearAllColumns()
    {
        var scopes = new List<string>();
        using (SqliteCommand cmd = db.Command("SELECT scope_key FROM view_settings"))
        using (SqliteDataReader reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                scopes.Add(reader.GetString(0));
            }
        }

        foreach (string scope in scopes)
        {
            Set(scope, Get(scope) with { Columns = null, ColumnWidths = null });
        }
    }
}

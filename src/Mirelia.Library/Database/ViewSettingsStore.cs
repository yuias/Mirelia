using System.Text.Json;
using System.Text.Json.Serialization;
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
}

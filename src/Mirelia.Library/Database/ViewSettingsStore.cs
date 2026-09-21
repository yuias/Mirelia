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

    /// <summary>
    /// Layout-style key (the app's layout registry owns the vocabulary), or
    /// null for the flat track list. Unknown keys — a style this build does
    /// not have — read as null rather than breaking the view.
    /// </summary>
    public string? Layout { get; init; }

    /// <summary>Sort preset, or null for the view's default.</summary>
    public TrackSortOrder? Sort { get; init; }

    /// <summary>
    /// Id of a user-defined sort order overriding <see cref="Sort"/>, or null
    /// when a preset is active. A reference to an order since deleted falls
    /// back to <see cref="Sort"/> rather than leaving the view unsorted.
    /// </summary>
    public long? CustomSort { get; init; }

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

    /// <summary>
    /// Ids of the expanded playlist folders. Used only under
    /// <see cref="ViewSettingsStore.PlaylistTreeScopeKey"/>, which is not a
    /// view: the ids are rows of this library, so the state cannot live in
    /// the app-level settings file.
    /// </summary>
    public List<long>? ExpandedFolders { get; init; }
}

/// <summary>
/// Persists <see cref="ViewSettings"/> per scope key (e.g. "source:music",
/// "playlist:42") in the <c>view_settings</c> table (schema v2).
/// </summary>
public sealed class ViewSettingsStore(LibraryDatabase db)
{
    /// <summary>
    /// Reserved scope key holding the playlist tree's expansion state. It is
    /// never a view, so nothing that writes view settings touches it.
    /// </summary>
    public const string PlaylistTreeScopeKey = "tree:playlists";

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

    /// <summary>Ids of the playlist folders left expanded; empty when none.</summary>
    public IReadOnlyList<long> GetExpandedFolders() =>
        Get(PlaylistTreeScopeKey).ExpandedFolders ?? [];

    /// <summary>Records which playlist folders are expanded.</summary>
    public void SetExpandedFolders(IEnumerable<long> ids) =>
        Set(PlaylistTreeScopeKey, new ViewSettings { ExpandedFolders = [.. ids] });

    /// <summary>
    /// Drops the saved column layout and widths of every scope, so all views
    /// fall back to the defaults. Sort state is deliberately left alone:
    /// this is a columns reset, not a view reset.
    /// </summary>
    public void ClearAllColumns() => ClearColumns(keepScopeKey: null);

    /// <summary>
    /// Drops the saved column layout and widths of every scope but
    /// <paramref name="keepScopeKey"/>. Views with no saved columns follow
    /// that scope, so this hands them its current layout without copying it —
    /// and they keep following it as it changes. Sort state is left alone,
    /// as in <see cref="ClearAllColumns"/>.
    /// </summary>
    public void ClearColumnsExcept(string keepScopeKey) => ClearColumns(keepScopeKey);

    private void ClearColumns(string? keepScopeKey)
    {
        var scopes = new List<string>();
        using (SqliteCommand cmd = db.Command("SELECT scope_key FROM view_settings"))
        using (SqliteDataReader reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                string scope = reader.GetString(0);
                if (scope != keepScopeKey)
                {
                    scopes.Add(scope);
                }
            }
        }

        // One transaction: a crash mid-loop would otherwise leave some views
        // cleared and the rest not, which reads as the command half-working.
        using LibraryDatabase.TransactionScope tx = db.BeginTransaction();
        foreach (string scope in scopes)
        {
            Set(scope, Get(scope) with { Columns = null, ColumnWidths = null });
        }

        tx.Commit();
    }
}

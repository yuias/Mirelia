using System.Text;
using Mirelia.Library.Export;

namespace Mirelia.Library.Sync;

/// <summary>What a sync is doing right now, for the progress UI.</summary>
/// <param name="Completed">Steps finished.</param>
/// <param name="Total">Steps in the whole run.</param>
/// <param name="BytesCopied">Bytes written so far.</param>
/// <param name="Current">Path being worked on.</param>
public readonly record struct SyncProgress(int Completed, int Total, long BytesCopied, string Current);

/// <summary>Counters of one completed run.</summary>
/// <param name="Copied">Files copied.</param>
/// <param name="Deleted">Files removed.</param>
/// <param name="PlaylistsWritten">Playlist files written.</param>
/// <param name="BytesCopied">Bytes written.</param>
/// <param name="Failures">Per-file errors; the run continues past them.</param>
public sealed record SyncResult(
    int Copied,
    int Deleted,
    int PlaylistsWritten,
    long BytesCopied,
    IReadOnlyList<string> Failures);

/// <summary>
/// Executes a <see cref="SyncPlan"/>. Everything it needs was decided by
/// <see cref="SyncPlanner"/>; when the transcode pipeline lands (M8+) only
/// the copy step changes.
/// </summary>
public sealed class SyncRunner(SyncTargetStore targets)
{
    /// <summary>
    /// Runs the plan against the two sinks. Copies come first so a playlist
    /// never references a file that is not there yet.
    /// </summary>
    public SyncResult Run(
        SyncPlan plan,
        ISyncFileSink musicSink,
        ISyncFileSink playlistSink,
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default)
    {
        SyncTargetConfig config = plan.Target.Config;
        var failures = new List<string>();
        int total = plan.Copies.Count + plan.Deletes.Count + plan.Playlists.Count;
        int completed = 0;
        int copied = 0, deleted = 0, playlistsWritten = 0;
        long bytes = 0;

        foreach (SyncCopy copy in plan.Copies)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new SyncProgress(completed, total, bytes, copy.File.RelPath));
            try
            {
                if (copy.PreviousRelPath is { } stale)
                {
                    musicSink.Delete(stale);
                }

                musicSink.WriteFile(copy.File.RelPath, copy.File.Track.SourcePath, ct);

                // Recorded only after the bytes land, so an interrupted run
                // re-copies rather than believing a file it never wrote.
                targets.RecordFile(plan.Target.Id, ToRecord(copy.File));
                copied++;
                bytes += copy.File.Track.SizeBytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{copy.File.RelPath}: {ex.Message}");
            }

            completed++;
        }

        foreach (SyncDelete delete in plan.Deletes)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new SyncProgress(completed, total, bytes, delete.RelPath));
            try
            {
                musicSink.Delete(delete.RelPath);
                targets.ForgetFile(plan.Target.Id, delete.TrackId);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{delete.RelPath}: {ex.Message}");
            }

            completed++;
        }

        Encoding encoding = config.PlaylistFormat == "m3u"
            ? M3u8Exporter.EncodingForCodepage(config.Codepage)
            : M3u8Exporter.Utf8NoBom;

        foreach (PlaylistExport export in plan.Playlists)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new SyncProgress(completed, total, bytes, export.RelPath));
            try
            {
                List<M3uEntry> entries = BuildEntries(plan, export, config);
                playlistSink.WriteBytes(export.RelPath, M3u8Exporter.Render(entries, encoding));
                playlistsWritten++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{export.RelPath}: {ex.Message}");
            }

            completed++;
        }

        progress?.Report(new SyncProgress(completed, total, bytes, string.Empty));
        return new SyncResult(copied, deleted, playlistsWritten, bytes, failures);
    }

    private static List<M3uEntry> BuildEntries(
        SyncPlan plan, PlaylistExport export, SyncTargetConfig config)
    {
        var entries = new List<M3uEntry>(export.TrackIds.Count);
        foreach (long trackId in export.TrackIds)
        {
            // Tracks whose source went missing were never planned a
            // destination; referencing them would only produce dead lines.
            if (!plan.Files.TryGetValue(trackId, out PlannedFile? file))
            {
                continue;
            }

            TrackLayoutInfo track = file.Track;
            string display = string.IsNullOrEmpty(track.Artist)
                ? track.Title ?? Path.GetFileNameWithoutExtension(track.SourcePath)
                : $"{track.Artist} - {track.Title}";

            entries.Add(new M3uEntry(
                SyncPaths.Reference(file.RelPath, export.RelPath, config),
                display,
                (int)Math.Round(track.DurationMs / 1000.0)));
        }

        return entries;
    }

    private static SyncedFile ToRecord(PlannedFile file) => new(
        file.Track.TrackId,
        file.RelPath,
        file.Track.SizeBytes,
        file.Track.MTimeUnix,
        file.Track.ContentHash);
}

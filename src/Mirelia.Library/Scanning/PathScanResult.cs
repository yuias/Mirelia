namespace Mirelia.Library.Scanning;

/// <summary>
/// Result of scanning an explicit list of paths: the usual counters plus the
/// track id each scanned file maps to, in scan order.
/// </summary>
/// <param name="Counts">Outcome counters, as returned by <see cref="LibraryScanner.ScanPaths"/>.</param>
/// <param name="TrackIds">
/// One id per Added, Updated or Unchanged file, in scan order. Skipped files
/// contribute nothing. A file scanned twice appears twice.
/// </param>
public sealed record PathScanResult(ScanResult Counts, IReadOnlyList<long> TrackIds);

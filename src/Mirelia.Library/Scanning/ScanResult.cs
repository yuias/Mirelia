namespace Mirelia.Library.Scanning;

/// <summary>Outcome counters of one scan pass.</summary>
/// <param name="Added">Tracks inserted.</param>
/// <param name="Updated">Tracks whose file changed and were re-read.</param>
/// <param name="Unchanged">Tracks skipped because size+mtime matched.</param>
/// <param name="Skipped">Files that failed to read or use unsupported codecs.</param>
public sealed record ScanResult(int Added, int Updated, int Unchanged, int Skipped)
{
    /// <summary>Total files considered.</summary>
    public int Total => Added + Updated + Unchanged + Skipped;
}

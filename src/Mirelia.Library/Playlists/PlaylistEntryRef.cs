namespace Mirelia.Library.Playlists;

/// <summary>
/// One static-playlist entry as the caller saw it: its position and the track
/// that position held. Removal checks both, so a stale view cannot remove the
/// wrong entry.
/// </summary>
/// <param name="Position">Zero-based entry position.</param>
/// <param name="TrackId">Track id the caller expects at that position.</param>
public readonly record struct PlaylistEntryRef(int Position, long TrackId);

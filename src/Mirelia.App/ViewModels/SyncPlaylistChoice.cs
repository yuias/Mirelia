using CommunityToolkit.Mvvm.ComponentModel;

namespace Mirelia.App.ViewModels;

/// <summary>One checkable playlist in a sync target's scope picker.</summary>
public sealed partial class SyncPlaylistChoice(long id, string label, bool selected) : ObservableObject
{
    /// <summary>Playlist id.</summary>
    public long Id { get; } = id;

    /// <summary>Display text, marking folders so their expansion is not a surprise.</summary>
    public string Label { get; } = label;

    [ObservableProperty]
    private bool _isSelected = selected;
}

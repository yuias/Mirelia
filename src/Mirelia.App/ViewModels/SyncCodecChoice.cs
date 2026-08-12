using CommunityToolkit.Mvvm.ComponentModel;

namespace Mirelia.App.ViewModels;

/// <summary>One checkable codec in a sync target's device profile.</summary>
public sealed partial class SyncCodecChoice(string codec, bool selected) : ObservableObject
{
    /// <summary>Codec name as <c>track.codec</c> spells it.</summary>
    public string Codec { get; } = codec;

    /// <summary>Display text.</summary>
    public string Label { get; } = codec.ToUpperInvariant();

    [ObservableProperty]
    private bool _isSelected = selected;
}

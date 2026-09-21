namespace Mirelia.Library.Sync;

/// <summary>What happens to one track on its way to a target.</summary>
public enum DeliveryKind
{
    /// <summary>Copy the file unchanged.</summary>
    AsIs,

    /// <summary>Re-encode before copying. Not implemented.</summary>
    Transcode,

    /// <summary>Leave the track behind.</summary>
    Skip,
}

/// <summary>
/// The decision for one track. <see cref="DeliveryKind.Transcode"/> carries
/// its target format; the other kinds leave those null.
/// </summary>
/// <param name="Kind">What to do.</param>
/// <param name="Codec">Codec to encode to, for <see cref="DeliveryKind.Transcode"/>.</param>
/// <param name="BitrateKbps">Bitrate to encode at, for <see cref="DeliveryKind.Transcode"/>.</param>
/// <param name="Reason">Short explanation, shown in the preflight summary.</param>
public readonly record struct DeliveryDecision(
    DeliveryKind Kind,
    string? Codec,
    int? BitrateKbps,
    string? Reason)
{
    /// <summary>Copy unchanged.</summary>
    public static DeliveryDecision AsIs => new(DeliveryKind.AsIs, null, null, null);

    /// <summary>Leave behind, for the stated reason.</summary>
    public static DeliveryDecision Skip(string reason) => new(DeliveryKind.Skip, null, null, reason);

    /// <summary>Re-encode. Reachable only once the transcode pipeline exists.</summary>
    public static DeliveryDecision Transcode(string codec, int bitrateKbps, string reason) =>
        new(DeliveryKind.Transcode, codec, bitrateKbps, reason);
}

/// <summary>
/// What a target can play. Empty or absent means "anything the library
/// holds", which is the right default for a plain SD card whose player is
/// unknown.
/// </summary>
public sealed record DeviceProfile
{
    /// <summary>
    /// Codecs the device decodes, in <c>track.codec</c> spelling. Null or
    /// empty accepts every codec.
    /// </summary>
    public List<string>? SupportedCodecs { get; init; }

    /// <summary>
    /// Leave lossless tracks behind — the usual reason being a card too small
    /// for them. Once transcoding lands this becomes "convert to AAC"
    /// instead.
    /// </summary>
    public bool SkipLossless { get; init; }
}

/// <summary>
/// Single place every "what format does this track arrive in" decision is
/// made, so new formats never have to touch the sync code.
/// </summary>
/// <remarks>
/// Today this returns <see cref="DeliveryKind.AsIs"/> or
/// <see cref="DeliveryKind.Skip"/> only. When the transcode pipeline lands,
/// the two <c>Skip</c> branches below become <c>Transcode</c> and nothing
/// else in sync has to change.
/// </remarks>
public static class DeliveryPolicy
{
    /// <summary>Decides how <paramref name="track"/> reaches a device.</summary>
    public static DeliveryDecision Decide(TrackLayoutInfo track, DeviceProfile? profile)
    {
        if (profile is null)
        {
            return DeliveryDecision.AsIs;
        }

        if (profile.SkipLossless && track.IsLossless)
        {
            return DeliveryDecision.Skip("lossless, and this target skips lossless");
        }

        if (profile.SupportedCodecs is { Count: > 0 } supported
            && !supported.Contains(track.Codec, StringComparer.OrdinalIgnoreCase))
        {
            return DeliveryDecision.Skip($"the target does not play {track.Codec}");
        }

        return DeliveryDecision.AsIs;
    }
}

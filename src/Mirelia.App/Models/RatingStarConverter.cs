using System.Globalization;
using Avalonia.Data.Converters;

namespace Mirelia.App.Models;

/// <summary>
/// Converts a <see cref="TrackRow.Rating"/> star count (0-5) plus the cell's
/// hover state into a filled or empty glyph for one star position. The
/// position (1-5) is passed as the converter parameter, so one converter
/// instance serves all five stars of a rating cell.
/// </summary>
public sealed class RatingStarConverter : IMultiValueConverter
{
    /// <summary>Shared stateless instance.</summary>
    public static readonly RatingStarConverter Instance = new();

    /// <summary>
    /// Expects the row's rating followed by the cell's hover state. An
    /// unrated row shows nothing at all until the pointer is over its cell,
    /// so a library with few ratings is not a wall of empty stars — the
    /// hover state is what keeps the cell clickable.
    /// </summary>
    public object? Convert(
        IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        int stars = values.Count > 0 && values[0] is int s ? s : 0;
        bool hovered = values.Count > 1 && values[1] is true;
        int position = parameter is int p ? p : 0;

        if (stars == 0 && !hovered)
        {
            return string.Empty;
        }

        return stars >= position ? "★" : "☆";
    }
}

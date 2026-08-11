using System.Globalization;
using Avalonia.Data.Converters;

namespace Mirelia.App.Models;

/// <summary>
/// Converts a <see cref="TrackRow.Rating"/> star count (0-5) into a filled
/// or empty glyph for one star position. The position (1-5) is passed as
/// the converter parameter, so one converter instance serves all five stars
/// of a rating cell.
/// </summary>
public sealed class RatingStarConverter : IValueConverter
{
    /// <summary>Shared stateless instance.</summary>
    public static readonly RatingStarConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int stars = value is int s ? s : 0;
        int position = parameter is int p ? p : 0;
        return stars >= position ? "★" : "☆";
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

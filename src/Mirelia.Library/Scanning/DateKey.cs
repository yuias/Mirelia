using System.Globalization;
using System.Text.RegularExpressions;

namespace Mirelia.Library.Scanning;

/// <summary>
/// Normalizes the wild variety of release-date spellings found in real tags
/// (<c>2025</c>, <c>2025/10/5</c>, <c>2025-08-12</c>, <c>2025.10.05</c>,
/// with or without a time suffix) into one sortable integer key:
/// <c>yyyymmdd</c>, with missing month/day as 01. Display keeps the raw
/// string; only ordering uses this.
/// </summary>
public static partial class DateKey
{
    [GeneratedRegex(@"^(\d{4})(?:[-/.](\d{1,2})(?:[-/.](\d{1,2}))?)?$")]
    private static partial Regex DatePattern();

    /// <summary>
    /// Computes the key from the raw date string, falling back to the
    /// numeric year (as January 1st) and then to null.
    /// </summary>
    public static int? Compute(string? dateText, int? year)
    {
        if (!string.IsNullOrWhiteSpace(dateText))
        {
            // Strip a time part ("2025-08-12T09:30", "2025-08-12 09:30").
            string datePart = dateText.Trim().Split('T', ' ')[0];
            Match match = DatePattern().Match(datePart);
            if (match.Success)
            {
                int y = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                int m = match.Groups[2].Success
                    ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)
                    : 1;
                int d = match.Groups[3].Success
                    ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)
                    : 1;

                // Out-of-range parts mean the string only looked like a
                // date; keep the year, drop the rest.
                if (m is < 1 or > 12)
                {
                    (m, d) = (1, 1);
                }
                else if (d is < 1 or > 31)
                {
                    d = 1;
                }

                return (y * 10_000) + (m * 100) + d;
            }
        }

        return year is { } fallback ? (fallback * 10_000) + 101 : null;
    }
}

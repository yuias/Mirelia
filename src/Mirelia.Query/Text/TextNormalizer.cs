using System.Text;

namespace Mirelia.Query.Text;

/// <summary>
/// Normalization used for both sort keys and search matching. The scanner
/// (write side) and the SQL compiler (read side) must agree on these rules,
/// which is why they live in one place.
/// </summary>
public static class TextNormalizer
{
    /// <summary>Default leading articles stripped from derived sort keys.</summary>
    public static readonly IReadOnlyList<string> DefaultArticles = ["the", "a", "an"];

    /// <summary>
    /// Normalizes for matching: NFKC (folds half/full width), katakana to
    /// hiragana, casefold. This is the semantics of the <c>=~</c> operator.
    /// </summary>
    public static string NormalizeSearch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string nfkc = value.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(nfkc.Length);
        foreach (char c in nfkc)
        {
            sb.Append(KatakanaToHiragana(c));
        }

        return sb.ToString().ToLowerInvariant().Trim();
    }

    /// <summary>
    /// Derives a sort key: <see cref="NormalizeSearch"/> plus stripping one
    /// leading article. Used only when the file carries no explicit sort tag.
    /// </summary>
    public static string DeriveSortKey(string? value, IReadOnlyList<string>? articles = null)
    {
        string normalized = NormalizeSearch(value);
        foreach (string article in articles ?? DefaultArticles)
        {
            if (normalized.Length > article.Length + 1
                && normalized.StartsWith(article, StringComparison.Ordinal)
                && normalized[article.Length] == ' ')
            {
                return normalized[(article.Length + 1)..].TrimStart();
            }
        }

        return normalized;
    }

    private static char KatakanaToHiragana(char c)
    {
        // U+30A1..U+30F6 are katakana with hiragana counterparts 0x60 below.
        return c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c;
    }
}

using Mirelia.Query.Text;
using Xunit;

namespace Mirelia.Query.Tests;

public class TextNormalizerTests
{
    [Theory]
    [InlineData("ABC", "abc")]
    [InlineData("ＡＢＣ", "abc")] // full-width Latin folds via NFKC
    [InlineData("カタカナ", "かたかな")] // katakana folds to hiragana
    [InlineData("ｶﾀｶﾅ", "かたかな")] // half-width katakana folds too
    [InlineData("ひらがな", "ひらがな")]
    [InlineData("１２３", "123")] // full-width digits
    [InlineData("  spaced  ", "spaced")]
    [InlineData("", "")]
    public void NormalizeSearchFoldsWidthKanaAndCase(string input, string expected)
    {
        Assert.Equal(expected, TextNormalizer.NormalizeSearch(input));
    }

    [Theory]
    [InlineData("The Beatles", "beatles")]
    [InlineData("A Day in the Life", "day in the life")]
    [InlineData("An Ending", "ending")]
    [InlineData("Theory of Everything", "theory of everything")] // no false article strip
    [InlineData("The", "the")] // article alone stays
    [InlineData("山田太郎", "山田太郎")]
    public void DeriveSortKeyStripsLeadingArticles(string input, string expected)
    {
        Assert.Equal(expected, TextNormalizer.DeriveSortKey(input));
    }

    [Fact]
    public void ProlongedSoundMarkIsPreserved()
    {
        // The chōonpu is not a katakana letter and must survive folding.
        Assert.Equal("すーぱー", TextNormalizer.NormalizeSearch("スーパー"));
    }
}

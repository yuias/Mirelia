using Mirelia.Library.Sync;
using Xunit;

namespace Mirelia.Library.Tests;

/// <summary>Pure layout rules: sanitization and template expansion.</summary>
public sealed class SyncLayoutTests
{
    private static TrackLayoutInfo Track(
        string? title = "Fixture Song",
        string? artist = "Fixture Artist",
        string? albumArtist = "Fixture Artist",
        string? album = "Fixture Album",
        int? trackNo = 3,
        int? discNo = 1,
        bool multiDisc = false,
        string sourcePath = @"X:\Music\Fixture Album\03 Fixture Song.flac") =>
        new(
            TrackId: 42,
            SourcePath: sourcePath,
            SizeBytes: 1000,
            MTimeUnix: 0,
            ContentHash: null,
            DurationMs: 240000,
            Codec: "flac",
            IsLossless: true,
            Title: title,
            Artist: artist,
            AlbumArtist: albumArtist,
            Album: album,
            AlbumId: 7,
            TrackNo: trackNo,
            DiscNo: discNo,
            MultiDisc: multiDisc,
            Genre: "Rock",
            Year: 2020,
            SourceRelative: @"Fixture Album\03 Fixture Song.flac");

    [Fact]
    public void DefaultTemplateRendersArtistAlbumTrackTitle()
    {
        string path = PathTemplate.Render(PathTemplate.Default, Track(), ReservedCharMode.Underscore);
        Assert.Equal("Fixture Artist/Fixture Album/03 Fixture Song.flac", path);
    }

    [Fact]
    public void DiscPrefixAppearsOnlyOnMultiDiscAlbums()
    {
        Assert.Equal(
            "Fixture Artist/Fixture Album/1-03 Fixture Song.flac",
            PathTemplate.Render(PathTemplate.Default, Track(multiDisc: true), ReservedCharMode.Underscore));
    }

    [Fact]
    public void MissingTagsFallBackRatherThanProducingEmptyComponents()
    {
        TrackLayoutInfo track = Track(title: null, artist: null, albumArtist: null, album: null, trackNo: null);
        string path = PathTemplate.Render(PathTemplate.Default, track, ReservedCharMode.Underscore);
        Assert.Equal("Unknown Artist/Unknown Album/03 Fixture Song.flac", path);
    }

    [Fact]
    public void AlbumArtistFallsBackToTrackArtist()
    {
        string path = PathTemplate.Render(
            PathTemplate.Default, Track(albumArtist: null), ReservedCharMode.Underscore);
        Assert.StartsWith("Fixture Artist/", path);
    }

    [Fact]
    public void MirrorSourcePreservesTheExistingLayout()
    {
        string path = PathTemplate.Render(
            PathTemplate.PresetMirrorSource, Track(), ReservedCharMode.Underscore);
        Assert.Equal("Fixture Album/03 Fixture Song.flac", path);
    }

    [Fact]
    public void UnknownTokensAreRejected()
    {
        Assert.Throws<PathTemplateException>(
            () => PathTemplate.Render("{Nope}/x", Track(), ReservedCharMode.Underscore));
        Assert.NotNull(PathTemplate.Validate("{Album"));
        Assert.Null(PathTemplate.Validate(PathTemplate.Default));
    }

    [Theory]
    [InlineData("AC/DC", "AC_DC")]
    [InlineData("What?", "What_")]
    [InlineData("Chapter: Two", "Chapter_ Two")]
    [InlineData("trailing. ", "trailing")]
    [InlineData("...", "_")]
    public void ReservedCharactersAndTrailingPunctuationAreReplaced(string raw, string expected)
    {
        Assert.Equal(expected, NameSanitizer.Component(raw, ReservedCharMode.Underscore));
    }

    [Fact]
    public void FullWidthModeKeepsTheGlyphReadable()
    {
        Assert.Equal("AC／DC", NameSanitizer.Component("AC/DC", ReservedCharMode.FullWidth));
    }

    [Fact]
    public void DeviceNamesAreEscapedEvenWithAnExtension()
    {
        Assert.Equal("NUL_", NameSanitizer.Component("NUL", ReservedCharMode.Underscore));
        Assert.Equal("con_.mp3", NameSanitizer.Component("con", ReservedCharMode.Underscore, ".mp3"));
    }

    [Fact]
    public void LongNamesTruncateAndStayDistinct()
    {
        string a = NameSanitizer.Component(new string('a', 80) + "one", ReservedCharMode.Underscore, ".flac");
        string b = NameSanitizer.Component(new string('a', 80) + "two", ReservedCharMode.Underscore, ".flac");

        Assert.NotEqual(a, b);
        Assert.EndsWith(".flac", a);
        // Budget plus the " ~xxxx" marker; the extension is outside the budget.
        Assert.Equal(NameSanitizer.MaxComponentLength + 6 + ".flac".Length, a.Length);
    }

    [Fact]
    public void ExtensionIsNotDuplicatedWhenTheTemplateSuppliesIt()
    {
        string path = PathTemplate.Render(
            "{Album}/{Title}.{Ext}", Track(), ReservedCharMode.Underscore);
        Assert.Equal("Fixture Album/Fixture Song.flac", path);
    }
}

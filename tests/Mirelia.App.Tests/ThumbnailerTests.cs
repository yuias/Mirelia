using Mirelia.App.Services;
using SkiaSharp;
using Xunit;

namespace Mirelia.App.Tests;

public sealed class ThumbnailerTests
{
    [Fact]
    public void OversizedCoversAreScaledToTheThumbnailBound()
    {
        byte[] source = MakePng(1600, 1600);

        byte[] thumbnail = Assert.IsType<byte[]>(Thumbnailer.Encode(source));
        SKImageInfo info = Describe(thumbnail);

        Assert.Equal(Thumbnailer.MaxEdge, Math.Max(info.Width, info.Height));
        // Album art is the reason this exists: full-size art in memory is what
        // made the cover cache enormous.
        Assert.True(
            thumbnail.Length < source.Length,
            $"thumbnail {thumbnail.Length} B was not smaller than source {source.Length} B");
    }

    [Fact]
    public void NonSquareCoversKeepTheirAspectRatio()
    {
        byte[] thumbnail = Assert.IsType<byte[]>(Thumbnailer.Encode(MakePng(1200, 600)));
        SKImageInfo info = Describe(thumbnail);

        Assert.Equal(Thumbnailer.MaxEdge, info.Width);
        Assert.Equal(Thumbnailer.MaxEdge / 2, info.Height);
    }

    [Fact]
    public void SmallCoversAreNotUpscaled()
    {
        byte[] thumbnail = Assert.IsType<byte[]>(Thumbnailer.Encode(MakePng(200, 200)));
        SKImageInfo info = Describe(thumbnail);

        Assert.Equal(200, info.Width);
        Assert.Equal(200, info.Height);
    }

    [Fact]
    public void AThumbnailIsSmallEnoughToCacheInBulk()
    {
        byte[] thumbnail = Assert.IsType<byte[]>(Thumbnailer.Encode(MakePhotographicPng(1600, 1600)));

        // The in-memory cache is bounded in bytes, so the per-cover size is
        // what decides how many covers fit. 200 KB would blow that budget.
        Assert.True(
            thumbnail.Length < 200 * 1024,
            $"thumbnail was {thumbnail.Length} B");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    public void UndecodableBytesYieldNoThumbnail(int length)
    {
        // Tag pictures are arbitrary bytes; a broken one must read as "no
        // cover", not throw into the block's load path.
        Assert.Null(Thumbnailer.Encode(new byte[length]));
    }

    private static SKImageInfo Describe(byte[] encoded)
    {
        using var data = SKData.CreateCopy(encoded);
        using SKCodec codec = SKCodec.Create(data);
        Assert.NotNull(codec);
        return codec.Info;
    }

    private static byte[] MakePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.CornflowerBlue);
            using var paint = new SKPaint { Color = SKColors.Goldenrod };
            canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) / 3f, paint);
        }

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Noisy content, so the size assertion is not passing on a flat image
    /// that any codec compresses to nothing.
    /// </summary>
    private static byte[] MakePhotographicPng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        var rng = new Random(42);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, new SKColor(
                    (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)));
            }
        }

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

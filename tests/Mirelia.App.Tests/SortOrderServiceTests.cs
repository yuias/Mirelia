using Mirelia.App.Services;
using Mirelia.Library.Database;
using Mirelia.Library.Querying;
using Xunit;

namespace Mirelia.App.Tests;

public sealed class SortOrderServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mirelia-sortorder-{Guid.NewGuid():N}");

    public SortOrderServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task SortOrdersRoundTripThroughTheService()
    {
        using var service = new LibraryService(Path.Combine(_root, "library.db"));
        Assert.Empty(await service.GetSortOrdersAsync());

        long byArtist = await service.CreateSortOrderAsync(
            "Album Artist, then Release",
            new SortSpec([new SortTerm("albumartist", false), new SortTerm("albumdate", true)]));
        long byLength = await service.CreateSortOrderAsync(
            "Longest First", SortSpec.Of("time", descending: true));

        List<SortOrderInfo> stored = await service.GetSortOrdersAsync();
        Assert.Equal([byArtist, byLength], stored.Select(o => o.Id));
        Assert.Equal(
            [new SortTerm("albumartist", false), new SortTerm("albumdate", true)],
            stored[0].Spec.Terms);

        await service.ReorderSortOrdersAsync([byLength, byArtist]);
        Assert.Equal([byLength, byArtist], (await service.GetSortOrdersAsync()).Select(o => o.Id));

        await service.DeleteSortOrderAsync(byLength);
        Assert.Equal([byArtist], (await service.GetSortOrdersAsync()).Select(o => o.Id));
    }

    [Fact]
    public async Task ADeletedOrderLeavesTheReferencingViewOnItsPreset()
    {
        using var service = new LibraryService(Path.Combine(_root, "library.db"));
        long id = await service.CreateSortOrderAsync("Mine", SortSpec.Of("rating", true));
        await service.SetViewSettingsAsync("playlist:1", new ViewSettings
        {
            Sort = TrackSortOrder.Album,
            CustomSort = id,
        });

        await service.DeleteSortOrderAsync(id);

        // The reference survives in the row; it is resolution that falls back,
        // so the view still knows which preset it came from.
        ViewSettings stored = await service.GetViewSettingsAsync("playlist:1");
        Assert.Equal(id, stored.CustomSort);
        Assert.Equal(TrackSortOrder.Album, stored.Sort);
        Assert.DoesNotContain(await service.GetSortOrdersAsync(), o => o.Id == id);
    }
}

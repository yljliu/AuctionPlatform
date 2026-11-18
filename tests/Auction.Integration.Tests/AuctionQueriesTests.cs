using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using AuctionEntity = Auction.Infrastructure.Persistence.Entities.Auction;

namespace Auction.Integration.Tests;

/// <summary>
/// One shared database for all tests in this class (they only read).
/// Categories: Watches > Vintage, plus Art. Auctions are listed in Seed below.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class AuctionQueriesTests(SqlServerFixture sql) : IAsyncLifetime
{
    private AuctionDbContext _db = null!;
    private int _watches, _vintage, _art;
    private Guid _seller1, _seller2;

    public async Task InitializeAsync()
    {
        _db = await sql.CreateEmptyContextAsync("queries");
        await _db.Database.MigrateAsync();
        await SeedAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private AuctionQueries Queries() => new(_db);

    private async Task SeedAsync()
    {
        var watches = new Category { Name = "Watches", Slug = "watches" };
        var art = new Category { Name = "Art", Slug = "art" };
        _db.Categories.AddRange(watches, art);
        await _db.SaveChangesAsync();
        var vintage = new Category { Name = "Vintage", Slug = "watches-vintage", ParentCategoryId = watches.CategoryId };
        _db.Categories.Add(vintage);
        await _db.SaveChangesAsync();
        (_watches, _vintage, _art) = (watches.CategoryId, vintage.CategoryId, art.CategoryId);

        _seller1 = Guid.NewGuid();
        _seller2 = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // title, category, seller, status, price, bids, ends in (minutes), condition
        Add("Omega Seamaster", _vintage, _seller1, AuctionStatus.Live, 150m, 3, 30, ItemCondition.Good, now);
        Add("Seiko diver", _watches, _seller1, AuctionStatus.Live, 90m, 0, 60, ItemCondition.LikeNew, now);
        Add("100% cotton print", _art, _seller2, AuctionStatus.Live, 40m, 1, 90, ItemCondition.New, now);
        Add("1000 coins", _art, _seller2, AuctionStatus.Live, 500m, 7, 120, ItemCondition.Fair, now);
        Add("Rolex box only", _vintage, _seller2, AuctionStatus.Live, 20m, 0, 150, ItemCondition.Good, now);
        Add("Future oil painting", _art, _seller1, AuctionStatus.Scheduled, 300m, 0, 600, ItemCondition.New, now);
        Add("Cancelled lamp", _art, _seller1, AuctionStatus.Cancelled, 10m, 0, 60, ItemCondition.Good, now);

        await _db.SaveChangesAsync();
    }

    private void Add(string title, int categoryId, Guid seller, AuctionStatus status, decimal price, int bids, int endsInMinutes,
        ItemCondition condition, DateTime now)
    {
        var item = new Item
        {
            ItemId = Guid.NewGuid(), SellerId = seller, CategoryId = categoryId, Title = title, Description = "Description of " + title,
            Condition = condition, ShippingType = ShippingType.FreeShipping, Status = ItemStatus.Published, CreatedAt = now, UpdatedAt = now,
        };
        var start = now.AddHours(-1);
        var end = now.AddMinutes(endsInMinutes);
        _db.Items.Add(item);
        _db.Auctions.Add(new AuctionEntity
        {
            AuctionId = Guid.NewGuid(), ItemId = item.ItemId, SellerId = seller, StartTime = start, ScheduledEndTime = end, EndTime = end,
            StartingPrice = price, CurrentPrice = bids > 0 ? price : null, BidCount = bids, FeeScheduleId = 1, Status = status,
            CreatedAt = now.AddMinutes(endsInMinutes), UpdatedAt = now,
        });
    }

    private static IEnumerable<string> Titles(AuctionCardPage page) => page.Rows.Select(r => r.Title);

    [Fact]
    public async Task Default_is_live_only_ending_soon_first()
    {
        var page = await Queries().SearchAsync(new AuctionSearch(AuctionStatus.Live), Paging.From(1, 20));

        Titles(page).Should().Equal("Omega Seamaster", "Seiko diver", "100% cotton print", "1000 coins", "Rolex box only");
        page.Total.Should().Be(5);
    }

    [Fact]
    public async Task Category_includes_its_children()
    {
        // The Api expands "Watches" to Watches + Vintage using the category tree.
        var page = await Queries().SearchAsync(new AuctionSearch(AuctionStatus.Live, CategoryIds: [_watches, _vintage]), Paging.From(1, 20));

        Titles(page).Should().BeEquivalentTo("Omega Seamaster", "Seiko diver", "Rolex box only");
    }

    [Fact]
    public async Task Child_category_alone_shows_only_its_items()
    {
        var page = await Queries().SearchAsync(new AuctionSearch(AuctionStatus.Live, CategoryIds: [_vintage]), Paging.From(1, 20));

        Titles(page).Should().BeEquivalentTo("Omega Seamaster", "Rolex box only");
    }

    [Fact]
    public async Task Empty_category_list_finds_nothing()
    {
        var page = await Queries().SearchAsync(new AuctionSearch(AuctionStatus.Live, CategoryIds: []), Paging.From(1, 20));

        page.Rows.Should().BeEmpty();
        page.Total.Should().Be(0);
    }

    [Fact]
    public async Task Price_filter_is_inclusive()
    {
        var page = await Queries().SearchAsync(new AuctionSearch(AuctionStatus.Live, MinPrice: 40m, MaxPrice: 150m), Paging.From(1, 20));

        Titles(page).Should().BeEquivalentTo("Omega Seamaster", "Seiko diver", "100% cotton print");
    }

    [Fact]
    public async Task Percent_in_the_search_is_literal()
    {
        var page = await Queries().SearchAsync(new AuctionSearch(AuctionStatus.Live, Words: SearchText.Words("100%")), Paging.From(1, 20));

        // "1000 coins" would match too if % were a wildcard.
        Titles(page).Should().Equal("100% cotton print");
    }

    [Fact]
    public async Task Every_word_must_match()
    {
        var page = await Queries().SearchAsync(new AuctionSearch(AuctionStatus.Live, Words: SearchText.Words("omega SEAMASTER")), Paging.From(1, 20));
        var none = await Queries().SearchAsync(new AuctionSearch(AuctionStatus.Live, Words: SearchText.Words("omega rolex")), Paging.From(1, 20));

        Titles(page).Should().Equal("Omega Seamaster");
        none.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_also_reads_the_description()
    {
        var page = await Queries().SearchAsync(new AuctionSearch(AuctionStatus.Live, Words: SearchText.Words("description of seiko")), Paging.From(1, 20));

        Titles(page).Should().Equal("Seiko diver");
    }

    [Fact]
    public async Task Paging_returns_the_right_slice_and_total()
    {
        var page1 = await Queries().SearchAsync(new AuctionSearch(AuctionStatus.Live), Paging.From(1, 2));
        var page3 = await Queries().SearchAsync(new AuctionSearch(AuctionStatus.Live), Paging.From(3, 2));

        page1.Rows.Should().HaveCount(2);
        page1.Total.Should().Be(5);
        Titles(page3).Should().Equal("Rolex box only");
        page3.Total.Should().Be(5);
    }

    [Fact]
    public async Task Page_past_the_end_is_empty_but_keeps_the_total()
    {
        var page = await Queries().SearchAsync(new AuctionSearch(AuctionStatus.Live), Paging.From(9, 20));

        page.Rows.Should().BeEmpty();
        page.Total.Should().Be(5);
    }

    [Theory]
    [InlineData(AuctionSortOrder.PriceAsc, "Rolex box only")]
    [InlineData(AuctionSortOrder.PriceDesc, "1000 coins")]
    [InlineData(AuctionSortOrder.MostBids, "1000 coins")]
    [InlineData(AuctionSortOrder.Newest, "Rolex box only")]
    public async Task Sort_orders(AuctionSortOrder sort, string first)
    {
        var page = await Queries().SearchAsync(new AuctionSearch(AuctionStatus.Live, Sort: sort), Paging.From(1, 20));

        page.Rows.First().Title.Should().Be(first);
    }

    [Fact]
    public async Task Seller_condition_and_ending_filters()
    {
        var bySeller = await Queries().SearchAsync(new AuctionSearch(AuctionStatus.Live, SellerId: _seller2), Paging.From(1, 20));
        var byCondition = await Queries().SearchAsync(new AuctionSearch(AuctionStatus.Live, Conditions: [ItemCondition.Good]), Paging.From(1, 20));
        var endingSoon = await Queries().SearchAsync(new AuctionSearch(AuctionStatus.Live, EndingBefore: DateTime.UtcNow.AddMinutes(45)), Paging.From(1, 20));

        Titles(bySeller).Should().BeEquivalentTo("100% cotton print", "1000 coins", "Rolex box only");
        Titles(byCondition).Should().BeEquivalentTo("Omega Seamaster", "Rolex box only");
        Titles(endingSoon).Should().Equal("Omega Seamaster");
    }

    [Fact]
    public async Task Scheduled_status_shows_upcoming_and_cancelled_never_shows()
    {
        var scheduled = await Queries().SearchAsync(new AuctionSearch(AuctionStatus.Scheduled), Paging.From(1, 20));

        Titles(scheduled).Should().Equal("Future oil painting");
        AuctionQueries.BrowsableStatuses.Should().NotContain([AuctionStatus.Cancelled, AuctionStatus.Draft]);
    }

    [Fact]
    public async Task Reserve_and_price_come_back_for_the_card()
    {
        var row = (await Queries().SearchAsync(new AuctionSearch(AuctionStatus.Live, Words: SearchText.Words("Seiko")), Paging.From(1, 20))).Rows.Single();

        row.Price.Should().Be(90m);   // starting price, no bids yet
        row.BidCount.Should().Be(0);
        row.ThumbId.Should().BeNull();
    }
}

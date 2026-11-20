using Auction.Domain.Catalog;
using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Seed;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>The demo catalog must pass the same rules a seller's listing does, and its photos must be real JPEGs.</summary>
public class DemoCatalogTests
{
    [Fact]
    public void Entries_follow_the_listing_rules()
    {
        DemoCatalog.Entries.Should().HaveCount(34);   // 24 + 6 Halloween + 4 Christmas
        DemoCatalog.Entries.Select(e => e.Key).Should().OnlyHaveUniqueItems();

        foreach (var e in DemoCatalog.Entries)
        {
            e.Title.Length.Should().BeInRange(1, ItemRules.TitleMax, e.Key);
            e.Description.Length.Should().BeInRange(1, ItemRules.DescriptionMax, e.Key);
            e.StartingPrice.Should().BePositive(e.Key);
            (e.Reserve ?? e.StartingPrice).Should().BeGreaterThanOrEqualTo(e.StartingPrice, e.Key);
            e.RunsFor.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(5), e.Key).And.BeLessThanOrEqualTo(TimeSpan.FromDays(14));
            e.StartsIn.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(1), e.Key);
            (e.Shipping == Domain.Enums.ShippingType.BuyerPays).Should().Be(e.ShippingCost != null, e.Key);
        }
    }

    [Fact]
    public void Seasonal_items_use_the_seasonal_categories_and_pictures()
    {
        DemoCatalog.Entries.Where(e => e.Category == "seasonal-halloween").Should().HaveCount(6).And.OnlyContain(e => e.Picture == PictureKind.Pumpkin);
        DemoCatalog.Entries.Where(e => e.Category == "seasonal-christmas").Should().HaveCount(4).And.OnlyContain(e => e.Picture == PictureKind.Ornament);
        Infrastructure.Simulation.SimulationAdmin.KindFor("seasonal-halloween").Should().Be(PictureKind.Pumpkin);
        Infrastructure.Simulation.SimulationAdmin.KindFor("seasonal-christmas").Should().Be(PictureKind.Ornament);
    }

    [Fact]
    public void Most_auctions_open_within_the_5_minute_load_window_plus_a_little()
    {
        DemoCatalog.Entries.Count(e => e.StartsIn <= TimeSpan.FromMinutes(7)).Should().BeGreaterThanOrEqualTo(18);
    }

    [Theory]
    [InlineData(PictureKind.Watch)]
    [InlineData(PictureKind.Painting)]
    [InlineData(PictureKind.Print)]
    [InlineData(PictureKind.Coin)]
    [InlineData(PictureKind.Card)]
    [InlineData(PictureKind.Pumpkin)]
    [InlineData(PictureKind.Ornament)]
    public void Pictures_are_jpegs_with_a_thumbnail_and_the_same_every_time(PictureKind kind)
    {
        var first = DemoPictures.Draw(kind, "k1", 0);
        var again = DemoPictures.Draw(kind, "k1", 0);
        var closeUp = DemoPictures.Draw(kind, "k1", 1);

        ImageSniffer.Detect(first).Should().Be(ImageSniffer.Jpeg);
        first.Length.Should().BeLessThan((int)ImageSniffer.MaxBytes);
        again.Should().Equal(first, "the same key draws the same picture");
        closeUp.Should().NotEqual(first);
        var (width, height, thumb) = Thumbnails.Make(first, ImageSniffer.Jpeg, ImageManager.ThumbSize, ImageManager.MaxPixels);
        (width, height).Should().Be((DemoPictures.Width, DemoPictures.Height));
        thumb.Length.Should().BeGreaterThan(0);
    }
}

using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

public class ItemRulesTests
{
    private static ItemInput Good() => new(
        CategoryId: 3,
        Title: "1960s Omega Seamaster",
        Description: "Runs well. Light wear.",
        Condition: "Good",
        WarrantyType: "None",
        WarrantyNote: null,
        ShippingType: "BuyerPays",
        ShippingCost: 12.50m,
        HandlingDays: 2,
        ReturnsAccepted: true,
        ReturnWindowDays: 14,
        ReturnShippingPaidBy: "Buyer");

    private static FieldIssue SingleIssue(ItemInput input)
    {
        var act = () => ItemRules.Validate(input);
        var ex = act.Should().Throw<DomainException>().Which;
        ex.Code.Should().Be(ErrorCodes.ValidationFailed);
        return ex.Fields.Should().ContainSingle().Which;
    }

    [Fact]
    public void Good_item_passes_and_parses_enums()
    {
        var valid = ItemRules.Validate(Good());

        valid.Condition.Should().Be(ItemCondition.Good);
        valid.ShippingType.Should().Be(ShippingType.BuyerPays);
        valid.ShippingCost.Should().Be(12.50m);
        valid.ReturnWindowDays.Should().Be((byte)14);
        valid.ReturnShippingPaidBy.Should().Be(ReturnShippingPaidBy.Buyer);
    }

    [Fact]
    public void Title_is_trimmed()
    {
        ItemRules.Validate(Good() with { Title = "  Watch  " }).Title.Should().Be("Watch");
    }

    [Fact]
    public void Missing_category()
    {
        SingleIssue(Good() with { CategoryId = 0 }).Field.Should().Be("category_id");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_title(string title)
    {
        SingleIssue(Good() with { Title = title }).Field.Should().Be("title");
    }

    [Fact]
    public void Title_over_120_characters()
    {
        SingleIssue(Good() with { Title = new string('a', 121) }).Field.Should().Be("title");
        ItemRules.Validate(Good() with { Title = new string('a', 120) }).Title.Should().HaveLength(120);
    }

    [Fact]
    public void Empty_description()
    {
        SingleIssue(Good() with { Description = "" }).Field.Should().Be("description");
    }

    [Theory]
    [InlineData("Shiny")]
    [InlineData("")]
    [InlineData("2")]
    public void Unknown_condition(string condition)
    {
        SingleIssue(Good() with { Condition = condition }).Field.Should().Be("condition");
    }

    [Fact]
    public void Enum_names_ignore_case()
    {
        ItemRules.Validate(Good() with { Condition = "likenew" }).Condition.Should().Be(ItemCondition.LikeNew);
    }

    [Fact]
    public void Unknown_warranty_type()
    {
        SingleIssue(Good() with { WarrantyType = "Lifetime" }).Field.Should().Be("warranty_type");
    }

    [Fact]
    public void Warranty_note_over_400_characters()
    {
        SingleIssue(Good() with { WarrantyNote = new string('n', 401) }).Field.Should().Be("warranty_note");
    }

    [Fact]
    public void Buyer_pays_needs_a_shipping_cost()
    {
        var issue = SingleIssue(Good() with { ShippingCost = null });

        issue.Field.Should().Be("shipping_cost");
        issue.Rule.Should().Be("required");
    }

    [Fact]
    public void Negative_shipping_cost()
    {
        SingleIssue(Good() with { ShippingCost = -1m }).Field.Should().Be("shipping_cost");
    }

    [Fact]
    public void Free_shipping_drops_the_cost()
    {
        var valid = ItemRules.Validate(Good() with { ShippingType = "FreeShipping", ShippingCost = 5m });

        valid.ShippingCost.Should().BeNull();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(31)]
    public void Handling_days_out_of_range(int days)
    {
        SingleIssue(Good() with { HandlingDays = days }).Field.Should().Be("handling_days");
    }

    [Fact]
    public void Returns_need_a_window()
    {
        var issue = SingleIssue(Good() with { ReturnWindowDays = null });

        issue.Field.Should().Be("return_window_days");
        issue.Rule.Should().Be("required");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void Return_window_out_of_range(int days)
    {
        SingleIssue(Good() with { ReturnWindowDays = days }).Field.Should().Be("return_window_days");
    }

    [Fact]
    public void No_returns_drops_the_window()
    {
        var valid = ItemRules.Validate(Good() with { ReturnsAccepted = false, ReturnWindowDays = 30 });

        valid.ReturnWindowDays.Should().BeNull();
        valid.ReturnShippingPaidBy.Should().BeNull();
    }

    [Fact]
    public void One_issue_per_bad_field()
    {
        var act = () => ItemRules.Validate(Good() with { Title = "", ShippingCost = null, ReturnWindowDays = null });

        act.Should().Throw<DomainException>().Which.Fields.Select(f => f.Field)
            .Should().Equal("title", "shipping_cost", "return_window_days");
    }

    [Fact]
    public void Locked_item_refuses_title_change()
    {
        var current = ItemRules.Validate(Good());
        var next = ItemRules.Validate(Good() with { Title = "New title" });

        var act = () => ItemRules.EnsureLockedFieldsUnchanged(true, current, next);

        act.Should().Throw<DomainException>().Which.Code.Should().Be(ErrorCodes.EditLocked);
    }

    [Fact]
    public void Locked_item_allows_shipping_change()
    {
        var current = ItemRules.Validate(Good());
        var next = ItemRules.Validate(Good() with { ShippingCost = 20m, HandlingDays = 5 });

        var act = () => ItemRules.EnsureLockedFieldsUnchanged(true, current, next);

        act.Should().NotThrow();
    }

    [Fact]
    public void Unlocked_item_allows_any_change()
    {
        var current = ItemRules.Validate(Good());
        var next = ItemRules.Validate(Good() with { Title = "New", CategoryId = 9 });

        var act = () => ItemRules.EnsureLockedFieldsUnchanged(false, current, next);

        act.Should().NotThrow();
    }
}

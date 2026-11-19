using Auction.Contracts;
using Auction.Domain.Catalog;
using Auction.Web.Services.Ui;

namespace Auction.Web.Services.Seller;

/// <summary>
/// The item form (spec 06 task 8, step 1). Checks mirror ItemRules so most mistakes show before sending;
/// the server still decides, and its field errors are shown on the same fields (design 6).
/// Keys in Errors are the server's field names (title, shipping_cost, ...).
/// </summary>
public sealed class ItemDraft
{
    public static readonly IReadOnlyList<string> Conditions = ["New", "LikeNew", "Good", "Fair", "ForParts"];
    public static readonly IReadOnlyList<string> Warranties = ["None", "Seller", "Manufacturer"];
    public static readonly IReadOnlyList<string> ShippingTypes = ["BuyerPays", "FreeShipping", "LocalPickupOnly"];
    public static readonly IReadOnlyList<string> ReturnPayers = ["Buyer", "Seller"];

    public int? CategoryId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Condition { get; set; } = "Good";
    public string WarrantyType { get; set; } = "None";
    public string WarrantyNote { get; set; } = "";
    public string ShippingType { get; set; } = "BuyerPays";
    public decimal? ShippingCost { get; set; }
    public int HandlingDays { get; set; } = 2;
    public bool ReturnsAccepted { get; set; }
    public int? ReturnWindowDays { get; set; }
    public string ReturnShippingPaidBy { get; set; } = "Buyer";

    public static ItemDraft From(Item item) => new()
    {
        CategoryId = item.CategoryId,
        Title = item.Title,
        Description = item.Description,
        Condition = item.Condition,
        WarrantyType = string.IsNullOrEmpty(item.WarrantyType) ? "None" : item.WarrantyType,
        WarrantyNote = item.WarrantyNote,
        ShippingType = item.ShippingType,
        ShippingCost = item.ShippingCost == null ? null : UiFormat.Amount(item.ShippingCost),
        HandlingDays = item.HandlingDays,
        ReturnsAccepted = item.ReturnsAccepted,
        ReturnWindowDays = item.ReturnWindowDays,
        ReturnShippingPaidBy = string.IsNullOrEmpty(item.ReturnShippingPaidBy) ? "Buyer" : item.ReturnShippingPaidBy,
    };

    public ItemFields ToFields()
    {
        var fields = new ItemFields
        {
            CategoryId = CategoryId ?? 0,
            Title = Title.Trim(),
            Description = Description.Trim(),
            Condition = Condition,
            WarrantyType = WarrantyType,
            WarrantyNote = WarrantyType == "None" ? "" : WarrantyNote.Trim(),
            ShippingType = ShippingType,
            HandlingDays = HandlingDays,
            ReturnsAccepted = ReturnsAccepted,
            ReturnShippingPaidBy = ReturnsAccepted ? ReturnShippingPaidBy : "",
        };
        if (ShippingType == "BuyerPays" && ShippingCost != null)
        {
            fields.ShippingCost = UiFormat.ToProto(ShippingCost.Value, "USD");
        }

        if (ReturnsAccepted && ReturnWindowDays != null)
        {
            fields.ReturnWindowDays = ReturnWindowDays;
        }

        return fields;
    }

    /// <summary>Field name to message; empty when it looks fine.</summary>
    public Dictionary<string, string> Validate()
    {
        var errors = new Dictionary<string, string>();
        if (CategoryId is null or <= 0)
        {
            errors["category_id"] = "Pick a category.";
        }

        if (Title.Trim().Length is 0 or > ItemRules.TitleMax)
        {
            errors["title"] = $"Title must be 1 to {ItemRules.TitleMax} characters.";
        }

        if (Description.Trim().Length is 0 or > ItemRules.DescriptionMax)
        {
            errors["description"] = $"Description must be 1 to {ItemRules.DescriptionMax} characters.";
        }

        if (WarrantyType != "None" && WarrantyNote.Length > ItemRules.WarrantyNoteMax)
        {
            errors["warranty_note"] = $"Warranty note must be at most {ItemRules.WarrantyNoteMax} characters.";
        }

        if (ShippingType == "BuyerPays" && ShippingCost == null)
        {
            errors["shipping_cost"] = "Enter a shipping cost when the buyer pays shipping.";
        }
        else if (ShippingCost < 0)
        {
            errors["shipping_cost"] = "Shipping cost cannot be negative.";
        }

        if (HandlingDays is < 0 or > ItemRules.HandlingDaysMax)
        {
            errors["handling_days"] = $"Handling days must be 0 to {ItemRules.HandlingDaysMax}.";
        }

        if (ReturnsAccepted && ReturnWindowDays == null)
        {
            errors["return_window_days"] = "Enter how many days the buyer has to return it.";
        }
        else if (ReturnsAccepted && ReturnWindowDays is < ItemRules.ReturnWindowMin or > ItemRules.ReturnWindowMax)
        {
            errors["return_window_days"] = $"Return window must be {ItemRules.ReturnWindowMin} to {ItemRules.ReturnWindowMax} days.";
        }

        return errors;
    }
}

/// <summary>
/// Pricing and time (step 3). The seller types a local date and time; the browser's offset turns it
/// into UTC (R5.2). Durations come from a fixed list (design 6).
/// </summary>
public sealed class AuctionDraft
{
    public static readonly IReadOnlyList<(TimeSpan Length, string Label)> Durations =
    [
        (TimeSpan.FromMinutes(5), "5 minutes"), (TimeSpan.FromMinutes(15), "15 minutes"), (TimeSpan.FromHours(1), "1 hour"),
        (TimeSpan.FromDays(1), "1 day"), (TimeSpan.FromDays(3), "3 days"), (TimeSpan.FromDays(7), "7 days"),
    ];

    public DateTime? StartDate { get; set; }
    public TimeSpan? StartTime { get; set; }
    public TimeSpan Duration { get; set; } = TimeSpan.FromDays(1);
    public decimal? StartingPrice { get; set; } = 10m;
    public decimal? ReservePrice { get; set; }
    public int SnipeWindowSec { get; set; } = AuctionRules.DefaultSnipeWindowSec;
    public int ExtensionSec { get; set; } = AuctionRules.DefaultExtensionSec;
    public int MaxTotalExtensionSec { get; set; } = AuctionRules.DefaultMaxTotalExtensionSec;

    /// <summary>A start 10 minutes from now, rounded up to 5 minutes, in the seller's local time.</summary>
    public static AuctionDraft Default(DateTimeOffset nowUtc, int offsetMinutes)
    {
        var local = nowUtc.ToOffset(TimeSpan.FromMinutes(offsetMinutes)).AddMinutes(10);
        var rounded = local.AddMinutes((5 - (local.Minute % 5)) % 5);
        return new AuctionDraft { StartDate = rounded.Date, StartTime = new TimeSpan(rounded.Hour, rounded.Minute, 0) };
    }

    public static AuctionDraft From(SellerAuction auction, int offsetMinutes)
    {
        var d = auction.Detail;
        var start = UiFormat.FromMs(d.Card.StartTimeMs).ToOffset(TimeSpan.FromMinutes(offsetMinutes));
        var length = UiFormat.FromMs(d.ScheduledEndTimeMs) - UiFormat.FromMs(d.Card.StartTimeMs);
        return new AuctionDraft
        {
            StartDate = start.Date,
            StartTime = new TimeSpan(start.Hour, start.Minute, 0),
            Duration = Durations.Select(x => x.Length).OrderBy(x => Math.Abs((x - length).TotalSeconds)).First(),
            StartingPrice = UiFormat.Amount(d.StartingPrice),
            ReservePrice = auction.ReservePrice == null ? null : UiFormat.Amount(auction.ReservePrice),
            SnipeWindowSec = d.SnipeWindowSec,
            ExtensionSec = d.ExtensionSec,
            MaxTotalExtensionSec = d.MaxTotalExtensionSec,
        };
    }

    /// <summary>Start in UTC, or null when the date or time is missing.</summary>
    public DateTimeOffset? StartUtc(int offsetMinutes) =>
        StartDate == null || StartTime == null
            ? null
            : new DateTimeOffset(StartDate.Value.Date + StartTime.Value, TimeSpan.FromMinutes(offsetMinutes)).ToUniversalTime();

    public DateTimeOffset? EndUtc(int offsetMinutes) => StartUtc(offsetMinutes) + Duration;

    /// <summary>What the seller gets at the reserve (or the starting price): price minus the seller fee (R5.2).</summary>
    public decimal EstimatedPayout(decimal sellerFeePct)
    {
        var price = ReservePrice ?? StartingPrice ?? 0m;
        return price - Math.Round(price * sellerFeePct / 100m, 2, MidpointRounding.AwayFromZero);
    }

    public AuctionFields ToFields(int offsetMinutes)
    {
        var start = StartUtc(offsetMinutes) ?? DateTimeOffset.UtcNow;
        var fields = new AuctionFields
        {
            StartTimeMs = start.ToUnixTimeMilliseconds(),
            EndTimeMs = (start + Duration).ToUnixTimeMilliseconds(),
            StartingPrice = UiFormat.ToProto(StartingPrice ?? 0m, "USD"),
            SnipeWindowSec = SnipeWindowSec,
            ExtensionSec = ExtensionSec,
            MaxTotalExtensionSec = MaxTotalExtensionSec,
            Currency = "USD",
        };
        if (ReservePrice != null)
        {
            fields.ReservePrice = UiFormat.ToProto(ReservePrice.Value, "USD");
        }

        return fields;
    }

    public Dictionary<string, string> Validate(DateTimeOffset nowUtc, int offsetMinutes)
    {
        var errors = new Dictionary<string, string>();
        var start = StartUtc(offsetMinutes);
        if (start == null)
        {
            errors["start_time_ms"] = "Pick a start date and time.";
        }
        else if (start < nowUtc.AddMinutes(1))
        {
            errors["start_time_ms"] = "Start must be at least 1 minute from now.";
        }

        if (StartingPrice is null or <= 0)
        {
            errors["starting_price"] = "Starting price must be more than 0.";
        }

        if (ReservePrice != null && StartingPrice != null && ReservePrice < StartingPrice)
        {
            errors["reserve_price"] = "Reserve must be at least the starting price.";
        }

        return errors;
    }
}

using Auction.Domain.Enums;
using Auction.Domain.Errors;

namespace Auction.Domain.Catalog;

/// <summary>What the seller sends to create or change an auction. Null means "use the default".</summary>
public sealed record AuctionInput(
    DateTime StartUtc,
    DateTime EndUtc,
    decimal StartingPrice,
    decimal? ReservePrice,
    int? SnipeWindowSec = null,
    int? ExtensionSec = null,
    int? MaxTotalExtensionSec = null,
    int? IncrementTableId = null,
    string? Currency = null);

/// <summary>An auction setup that passed every rule, with defaults filled in.</summary>
public sealed record AuctionSettings(
    DateTime StartUtc,
    DateTime EndUtc,
    decimal StartingPrice,
    decimal? ReservePrice,
    int SnipeWindowSec,
    int ExtensionSec,
    int MaxTotalExtensionSec,
    int IncrementTableId,
    int FeeScheduleId,
    string Currency);

public enum CancelPath
{
    /// <summary>Not started yet: SQL only.</summary>
    Scheduled,

    /// <summary>Live with no bids: Redis must agree first (race with a new bid).</summary>
    LiveWithoutBids,
}

/// <summary>Rules for create, edit and cancel (R4, R5).</summary>
public static class AuctionRules
{
    public static readonly TimeSpan MinLeadTime = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromDays(14);

    public const int DefaultSnipeWindowSec = 60;
    public const int DefaultExtensionSec = 60;
    public const int DefaultMaxTotalExtensionSec = 3600;
    public const int DefaultIncrementTableId = 1;
    public const int DefaultFeeScheduleId = 1;
    public const string DefaultCurrency = "USD";

    /// <summary>Returns the settings with defaults, or throws VALIDATION_FAILED with one issue per bad field.</summary>
    public static AuctionSettings Validate(AuctionInput input, DateTime nowUtc)
    {
        var issues = new List<FieldIssue>();

        if (input.StartUtc < nowUtc + MinLeadTime)
        {
            issues.Add(new FieldIssue("start_time_ms", "min_lead", "Start must be at least 1 minute from now."));
        }

        var duration = input.EndUtc - input.StartUtc;
        if (duration < MinDuration || duration > MaxDuration)
        {
            issues.Add(new FieldIssue("end_time_ms", "duration", "The auction must run between 5 minutes and 14 days."));
        }

        if (input.StartingPrice <= 0)
        {
            issues.Add(new FieldIssue("starting_price", "min", "Starting price must be more than 0."));
        }

        if (input.ReservePrice != null && input.ReservePrice < input.StartingPrice)
        {
            issues.Add(new FieldIssue("reserve_price", "min", "Reserve must be at least the starting price."));
        }

        var snipe = input.SnipeWindowSec ?? DefaultSnipeWindowSec;
        var extension = input.ExtensionSec ?? DefaultExtensionSec;
        var maxExtension = input.MaxTotalExtensionSec ?? DefaultMaxTotalExtensionSec;
        CheckRange(snipe, 0, 600, "snipe_window_sec", issues);
        CheckRange(extension, 0, 600, "extension_sec", issues);
        CheckRange(maxExtension, 0, 86400, "max_total_extension_sec", issues);

        var currency = string.IsNullOrWhiteSpace(input.Currency) ? DefaultCurrency : input.Currency.Trim().ToUpperInvariant();
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
        {
            issues.Add(new FieldIssue("currency", "format", "Currency must be a 3 letter code like USD."));
        }

        if (issues.Count > 0)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Some auction fields are not valid.", issues);
        }

        return new AuctionSettings(
            input.StartUtc, input.EndUtc, Money.Round(input.StartingPrice),
            input.ReservePrice == null ? null : Money.Round(input.ReservePrice.Value),
            snipe, extension, maxExtension,
            input.IncrementTableId ?? DefaultIncrementTableId, DefaultFeeScheduleId, currency);
    }

    /// <summary>Only Scheduled auctions can be edited (R5.1, R5.3).</summary>
    public static void EnsureEditable(AuctionStatus status, int bidCount)
    {
        if (status == AuctionStatus.Scheduled)
        {
            return;
        }

        if (bidCount > 0)
        {
            throw new DomainException(ErrorCodes.EditLocked, "This auction has bids, so it cannot be changed.");
        }

        throw new DomainException(ErrorCodes.InvalidState, "Only auctions that have not started can be changed.");
    }

    /// <summary>Seller cancel: Scheduled, or Live with no bids (R5.2, R5.3).</summary>
    public static CancelPath EnsureCancellable(AuctionStatus status, int bidCount)
    {
        if (status == AuctionStatus.Scheduled)
        {
            return CancelPath.Scheduled;
        }

        if (status == AuctionStatus.Live && bidCount == 0)
        {
            return CancelPath.LiveWithoutBids;
        }

        throw new DomainException(ErrorCodes.InvalidState,
            bidCount > 0 ? "This auction has bids, so it cannot be cancelled." : "This auction has ended or is already closed.");
    }

    /// <summary>A new auction may be made for Draft or Unsold items only.</summary>
    public static void EnsureItemCanBeListed(ItemStatus itemStatus)
    {
        if (itemStatus is not (ItemStatus.Draft or ItemStatus.Unsold))
        {
            throw new DomainException(ErrorCodes.InvalidState, "This item already has an open auction or is sold.");
        }
    }

    private static void CheckRange(int value, int min, int max, string field, List<FieldIssue> issues)
    {
        if (value < min || value > max)
        {
            issues.Add(new FieldIssue(field, "range", $"Must be {min} to {max}."));
        }
    }
}

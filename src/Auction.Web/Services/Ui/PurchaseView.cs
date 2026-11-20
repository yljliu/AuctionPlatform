using Auction.Contracts;

namespace Auction.Web.Services.Ui;

/// <summary>What the Purchases and Sales pages show for one settlement (spec 10 R4.1, R4.5, R5.7, R6.1). Pure, so it is unit tested.</summary>
public static class PurchaseView
{
    /// <summary>The Pay button is on while the purchase is unpaid and not past its deadline.</summary>
    public static bool CanPay(SettlementRow row, DateTimeOffset now) =>
        row.Status == "PendingPayment" && row.PaymentDeadlineMs > now.ToUnixTimeMilliseconds();

    public static bool IsPaid(SettlementRow row) => !string.IsNullOrEmpty(row.PaymentId);

    /// <summary>The buyer's words for the status.</summary>
    public static string BuyerStatus(SettlementRow row, DateTimeOffset now) => row.Status switch
    {
        "PendingPayment" when !CanPay(row, now) => "Not paid (deadline passed)",
        "PendingPayment" => "Awaiting payment",
        "PaidInFull" => "Paid in full",
        "Defaulted" => "Not paid (deadline passed)",
        var s => UiFormat.Words(s),
    };

    /// <summary>The seller's words for the status, with the second-chance offer when there is one.</summary>
    public static string SellerStatus(SettlementRow row) => (row.Status, row.OfferStatus) switch
    {
        ("PendingPayment", _) => "Awaiting payment",
        ("PaidInFull", _) => "Paid in full",
        ("Defaulted", "Open") => "Buyer did not pay · second-chance offer sent",
        ("Defaulted", "Accepted") => "Buyer did not pay · offer accepted",
        ("Defaulted", "Declined") => "Buyer did not pay · offer declined",
        ("Defaulted", "Expired") => "Buyer did not pay · offer expired",
        ("Defaulted", _) => "Buyer did not pay",
        var (s, _) => UiFormat.Words(s),
    };

    /// <summary>"Strikes: 1 of 3" for the account menu; null when there are none (R5.7).</summary>
    public static string? Strikes(int active) => active > 0 ? $"Strikes: {Math.Min(active, 3)} of 3" : null;
}

/// <summary>Admin appeals page rules (spec 10 R7.6).</summary>
public static class AppealRules
{
    /// <summary>Reject needs a note for the user.</summary>
    public static bool CanReject(string? note) => !string.IsNullOrWhiteSpace(note);
}

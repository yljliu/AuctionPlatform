using Auction.Contracts;
using Auction.Web.Services.Ui;
using FluentAssertions;
using Xunit;

namespace Auction.Web.Tests;

/// <summary>Spec 10 task 7: what the Purchases, Sales, account menu and admin appeals pages show.</summary>
public class PurchaseViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static SettlementRow Row(string status, double deadlineInHours = 24, string paymentId = "", string offerStatus = "") => new()
    {
        SettlementId = Guid.NewGuid().ToString(),
        Status = status,
        PaymentDeadlineMs = Now.AddHours(deadlineInHours).ToUnixTimeMilliseconds(),
        PaymentId = paymentId,
        OfferStatus = offerStatus,
    };

    [Fact]
    public void Pay_is_on_only_while_unpaid_and_before_the_deadline()
    {
        PurchaseView.CanPay(Row("PendingPayment"), Now).Should().BeTrue();
        PurchaseView.CanPay(Row("PendingPayment", deadlineInHours: -1), Now).Should().BeFalse();
        PurchaseView.CanPay(Row("PaidInFull", paymentId: Guid.NewGuid().ToString()), Now).Should().BeFalse();
        PurchaseView.CanPay(Row("Defaulted"), Now).Should().BeFalse();
    }

    [Fact]
    public void A_paid_row_shows_the_receipt() =>
        PurchaseView.IsPaid(Row("PaidInFull", paymentId: Guid.NewGuid().ToString())).Should().BeTrue();

    [Theory]
    [InlineData("PendingPayment", 24, "Awaiting payment")]
    [InlineData("PendingPayment", -1, "Not paid (deadline passed)")]
    [InlineData("PaidInFull", 24, "Paid in full")]
    [InlineData("Defaulted", -1, "Not paid (deadline passed)")]
    public void Buyer_words(string status, double hours, string words) =>
        PurchaseView.BuyerStatus(Row(status, hours), Now).Should().Be(words);

    [Theory]
    [InlineData("PaidInFull", "", "Paid in full")]
    [InlineData("Defaulted", "", "Buyer did not pay")]
    [InlineData("Defaulted", "Open", "Buyer did not pay · second-chance offer sent")]
    [InlineData("Defaulted", "Accepted", "Buyer did not pay · offer accepted")]
    [InlineData("Defaulted", "Expired", "Buyer did not pay · offer expired")]
    public void Seller_words(string status, string offer, string words) =>
        PurchaseView.SellerStatus(Row(status, offerStatus: offer)).Should().Be(words);

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "Strikes: 1 of 3")]
    [InlineData(3, "Strikes: 3 of 3")]
    [InlineData(4, "Strikes: 3 of 3")]
    public void Strikes_in_the_account_menu(int active, string? text) => PurchaseView.Strikes(active).Should().Be(text);

    [Theory]
    [InlineData(null, false)]
    [InlineData("  ", false)]
    [InlineData("Three unpaid wins.", true)]
    public void Reject_needs_a_note(string? note, bool ok) => AppealRules.CanReject(note).Should().Be(ok);
}

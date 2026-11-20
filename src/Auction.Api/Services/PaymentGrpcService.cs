using Auction.Api.Security;
using Auction.Contracts;
using Auction.Domain.Catalog;
using Auction.Infrastructure.Payments;
using Grpc.Core;

namespace Auction.Api.Services;

/// <summary>The fake payment API (spec 10 task 2). Signed in only; PaymentManager checks the buyer.</summary>
[SignedIn]
public sealed class PaymentGrpcService(PaymentManager payments, SecondChanceManager offers) : PaymentService.PaymentServiceBase
{
    public override async Task<SecondChance> SendSecondChance(SendSecondChanceRequest request, ServerCallContext context) =>
        ToProto(await offers.SendAsync(context.GetUserId(), CatalogMapper.ParseId(request.SettlementId, "settlement_id"), context.CancellationToken));

    public override async Task<SecondChance> GetSecondChance(SecondChanceRequest request, ServerCallContext context) =>
        ToProto(await offers.GetAsync(context.GetUserId(), CatalogMapper.ParseId(request.OfferId, "offer_id"), context.CancellationToken));

    public override async Task<SecondChance> AcceptSecondChance(SecondChanceRequest request, ServerCallContext context) =>
        ToProto(await offers.AcceptAsync(context.GetUserId(), CatalogMapper.ParseId(request.OfferId, "offer_id"), context.CancellationToken));

    public override async Task<SecondChance> DeclineSecondChance(SecondChanceRequest request, ServerCallContext context) =>
        ToProto(await offers.DeclineAsync(context.GetUserId(), CatalogMapper.ParseId(request.OfferId, "offer_id"), context.CancellationToken));

    public static SecondChance ToProto(SecondChanceView v) => new()
    {
        OfferId = v.Offer.OfferId.ToString(),
        DefaultedSettlementId = v.Offer.DefaultedSettlementId.ToString(),
        Auction = AuctionMapper.ToCard(v.Defaulted.Card),
        Amount = new Domain.Money(v.Offer.Amount, v.Defaulted.Card.Currency).ToProto(),
        Status = v.Offer.Status.ToString(),
        ExpiresAtMs = CatalogMapper.ToUnixMs(v.Offer.ExpiresAt),
        NewSettlementId = v.Offer.NewSettlementId?.ToString() ?? "",
        BidderId = v.Offer.BidderId.ToString(),
    };

    public override async Task<SettlementRow> GetPayable(GetPayableRequest request, ServerCallContext context)
    {
        var row = await payments.GetPayableAsync(context.GetUserId(), CatalogMapper.ParseId(request.SettlementId, "settlement_id"), context.CancellationToken);
        return AuctionMapper.ToSettlementRow(row, showBuyer: false);
    }

    public override async Task<PaymentReceipt> PayDemo(PayDemoRequest request, ServerCallContext context)
    {
        var card = request.Card ?? new DemoCard();
        var receipt = await payments.PayDemoAsync(
            context.GetUserId(),
            CatalogMapper.ParseId(request.SettlementId, "settlement_id"),
            new DemoCardInput(card.Number, card.Expiry, card.Cvc, card.Name),
            Guid.TryParse(request.IdempotencyKey, out var key) ? key : Guid.Empty,
            context.CancellationToken);
        return ToProto(receipt);
    }

    public override async Task<PaymentReceipt> GetReceipt(GetReceiptRequest request, ServerCallContext context) =>
        ToProto(await payments.GetReceiptAsync(context.GetUserId(), CatalogMapper.ParseId(request.SettlementId, "settlement_id"), context.CancellationToken));

    public static PaymentReceipt ToProto(PaymentReceiptView r) => new()
    {
        PaymentId = r.PaymentId.ToString(),
        Reference = r.Reference,
        SettlementId = r.Settlement.SettlementId.ToString(),
        Auction = AuctionMapper.ToCard(r.Settlement.Card),
        Amount = new Domain.Money(r.Amount, r.Settlement.Card.Currency).ToProto(),
        CardLabel = r.CardLabel,
        PaidAtMs = CatalogMapper.ToUnixMs(r.PaidAt),
        Repeat = r.Repeat,
    };
}

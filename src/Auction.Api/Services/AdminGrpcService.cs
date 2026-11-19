using Auction.Api.Security;
using Auction.Contracts;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Identity;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Auction.Api.Services;

[RequireRole(Role.Admin)]
public sealed class AdminGrpcService(UserAdminManager admin, AuctionControlManager auctions) : AdminService.AdminServiceBase
{
    public override async Task<Empty> BanUser(BanUserRequest request, ServerCallContext context)
    {
        await admin.BanAsync(context.GetUserId(), ParseUserId(request.UserId), request.Reason,
            context.GetCorrelationId(), context.CancellationToken);
        return new Empty();
    }

    public override async Task<Empty> SuspendUser(SuspendUserRequest request, ServerCallContext context)
    {
        var until = DateTimeOffset.FromUnixTimeMilliseconds(request.UntilMs).UtcDateTime;
        await admin.SuspendAsync(context.GetUserId(), ParseUserId(request.UserId), until, request.Reason,
            context.GetCorrelationId(), context.CancellationToken);
        return new Empty();
    }

    public override async Task<Empty> ForceCancelAuction(AuctionActionRequest request, ServerCallContext context)
    {
        await auctions.ForceCancelAsync(context.GetUserId(), ParseAuctionId(request.AuctionId), request.Reason,
            context.GetCorrelationId(), context.CancellationToken);
        return new Empty();
    }

    public override async Task<Empty> ForceCloseAuction(AuctionActionRequest request, ServerCallContext context)
    {
        await auctions.ForceCloseAsync(context.GetUserId(), ParseAuctionId(request.AuctionId), request.Reason,
            context.GetCorrelationId(), context.CancellationToken);
        return new Empty();
    }

    public override async Task<Empty> FreezeAuction(AuctionActionRequest request, ServerCallContext context)
    {
        await auctions.FreezeAsync(context.GetUserId(), ParseAuctionId(request.AuctionId), request.Reason,
            context.GetCorrelationId(), context.CancellationToken);
        return new Empty();
    }

    public override async Task<Empty> ResumeAuction(AuctionActionRequest request, ServerCallContext context)
    {
        await auctions.ResumeAsync(context.GetUserId(), ParseAuctionId(request.AuctionId), request.Reason,
            context.GetCorrelationId(), context.CancellationToken);
        return new Empty();
    }

    private static Guid ParseAuctionId(string value) =>
        Guid.TryParse(value, out var id)
            ? id
            : throw new DomainException(ErrorCodes.ValidationFailed, "auction_id is not a valid id.",
                [new FieldIssue("auction_id", "format", "Must be a UUID.")]);

    private static Guid ParseUserId(string value) =>
        Guid.TryParse(value, out var id)
            ? id
            : throw new DomainException(ErrorCodes.ValidationFailed, "user_id is not a valid id.",
                [new FieldIssue("user_id", "format", "Must be a UUID.")]);
}

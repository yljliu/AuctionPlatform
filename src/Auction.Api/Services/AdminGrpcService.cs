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
public sealed class AdminGrpcService(UserAdminManager admin, AuctionControlManager auctions, Infrastructure.Admin.AdminQueries queries, AuctionBrowser browser) : AdminService.AdminServiceBase
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

    public override async Task<Empty> ReinstateUser(ReinstateUserRequest request, ServerCallContext context)
    {
        await admin.ReinstateAsync(context.GetUserId(), ParseUserId(request.UserId), request.Reason,
            context.GetCorrelationId(), context.CancellationToken);
        return new Empty();
    }

    public override async Task<SearchUsersResponse> SearchUsers(SearchUsersRequest request, ServerCallContext context)
    {
        var (users, total) = await queries.SearchUsersAsync(request.Query, Domain.Catalog.Paging.From(request.Page, request.PageSize), context.CancellationToken);
        var response = new SearchUsersResponse { Total = total };
        foreach (var u in users)
        {
            var row = new AdminUserRow
            {
                UserId = u.UserId.ToString(),
                Username = u.Username,
                Email = u.Email,
                Status = u.Status.ToString(),
                SuspendedUntilMs = u.SuspendedUntil == null ? 0 : CatalogMapper.ToUnixMs(u.SuspendedUntil.Value),
                CreatedAtMs = CatalogMapper.ToUnixMs(u.CreatedAt),
            };
            row.Roles.AddRange(u.Roles.Select(r => r.ToString()));
            response.Users.Add(row);
        }

        return response;
    }

    /// <summary>Any status, Frozen and Cancelled too. Live ones get Redis prices like the public list.</summary>
    public override async Task<ListAuctionsResponse> ListAuctions(AdminListAuctionsRequest request, ServerCallContext context)
    {
        var status = AuctionStatus.Live;
        if (!string.IsNullOrWhiteSpace(request.Status) && !Domain.Catalog.EnumText.TryParse(request.Status, out status))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Unknown status.",
                [new FieldIssue("status", "one_of", "Use an auction status such as Live or Frozen.")]);
        }

        var words = string.IsNullOrWhiteSpace(request.Query) ? null : Domain.Catalog.SearchText.Words(request.Query);
        var paging = Domain.Catalog.Paging.From(request.Page, request.PageSize);
        var result = await browser.BrowseAsync(new AuctionSearch(status, Words: words, Sort: AuctionSortOrder.EndingSoon), paging,
            viewerId: null, context.CancellationToken);

        var response = new ListAuctionsResponse { Total = result.Total, Page = paging.Page, PageSize = paging.Size, PricesDelayed = result.PricesDelayed };
        response.Auctions.AddRange(result.Rows.Select(r => AuctionMapper.ToCard(r, false, result.PricesDelayed)));
        return response;
    }

    public override async Task<AdminStats> GetStats(GetStatsRequest request, ServerCallContext context)
    {
        var s = await queries.GetStatsAsync(context.CancellationToken);
        var stats = new AdminStats
        {
            LiveAuctions = s.LiveAuctions,
            ScheduledAuctions = s.ScheduledAuctions,
            SealedAuctions = s.SealedAuctions,
            FrozenAuctions = s.FrozenAuctions,
            Users = s.Users,
            BidsLast15Min = s.BidsLast15Min,
            SqlOk = s.SqlOk,
            RedisOk = s.RedisOk,
            RedisLatencyMs = s.RedisLatencyMs,
            LifecycleLeader = s.LifecycleLeader,
        };
        stats.BidsPerMinute.AddRange(s.BidsPerMinute);
        return stats;
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

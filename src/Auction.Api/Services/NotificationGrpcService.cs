using Auction.Api.Live;
using Auction.Api.Security;
using Auction.Contracts;
using Auction.Domain.Catalog;
using Auction.Infrastructure.Notify;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Auction.Api.Services;

/// <summary>Notification calls and the live bell feed (spec 07 tasks 4 and 5). Signed in only; always the caller's own.</summary>
[SignedIn]
public sealed class NotificationGrpcService(NotificationStore store, UserEventHub hub, TimeProvider clock) : NotificationService.NotificationServiceBase
{
    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(15);

    public override async Task<ListNotificationsResponse> ListNotifications(ListNotificationsRequest request, ServerCallContext context)
    {
        var page = await store.ListAsync(context.GetUserId(), request.UnreadOnly, Paging.From(request.Page, request.PageSize), context.CancellationToken);
        var response = new ListNotificationsResponse { Total = page.Total, Unread = page.Unread };
        response.Items.AddRange(page.Items.Select(ToProto));
        return response;
    }

    public override async Task<UnreadCount> MarkRead(MarkReadRequest request, ServerCallContext context) =>
        new() { Unread = await store.MarkReadAsync(context.GetUserId(), request.NotificationIds, context.CancellationToken) };

    public override async Task<UnreadCount> MarkAllRead(Empty request, ServerCallContext context) =>
        new() { Unread = await store.MarkAllReadAsync(context.GetUserId(), context.CancellationToken) };

    public override async Task<UnreadCount> GetUnreadCount(Empty request, ServerCallContext context) =>
        new() { Unread = await store.UnreadCountAsync(context.GetUserId(), context.CancellationToken) };

    /// <summary>New notifications as they are made (Pub/Sub), with a heartbeat every 15 s.</summary>
    public override async Task SubscribeUser(SubscribeUserRequest request, IServerStreamWriter<UserEvent> responseStream, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        await using var subscription = await hub.SubscribeAsync(context.GetUserId());
        await responseStream.WriteAsync(Heartbeat(), ct);   // "connected"

        while (!ct.IsCancellationRequested)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(HeartbeatEvery);
            try
            {
                var message = await subscription.Reader.ReadAsync(wait.Token);
                await responseStream.WriteAsync(new UserEvent { ServerTsMs = Now, Notification = ToProto(message) }, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await responseStream.WriteAsync(Heartbeat(), ct);
            }
            catch (OperationCanceledException)
            {
                break;   // the browser left
            }
        }
    }

    public static NotificationItem ToProto(NotificationMessage m) => new()
    {
        NotificationId = m.NotificationId,
        Type = m.Type.ToString(),
        Title = m.Title,
        Body = m.Body ?? "",
        Link = m.Link ?? "",
        AuctionId = m.AuctionId?.ToString() ?? "",
        Read = m.Read,
        CreatedAtMs = m.CreatedAtMs,
    };

    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    private UserEvent Heartbeat() => new() { ServerTsMs = Now, Heartbeat = new UserHeartbeat() };
}

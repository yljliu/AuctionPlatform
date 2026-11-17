using System.Diagnostics;
using Auction.Contracts.Errors;
using Serilog.Context;

namespace Auction.Api.Security;

/// <summary>
/// Gives every request a correlation id (R6.3): the x-correlation-id header if sent,
/// otherwise the trace id. It goes into the log scope and back in the response header.
/// </summary>
public sealed class CorrelationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var id = context.Request.Headers[RpcErrors.CorrelationHeader].ToString();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
        {
            id = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        }

        context.Items[CallContextExtensions.CorrelationItem] = id;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[RpcErrors.CorrelationHeader] = id;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", id))
        {
            await next(context);
        }
    }
}

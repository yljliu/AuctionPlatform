using Auction.Api.Security;
using Auction.Contracts.Errors;
using Auction.Domain.Errors;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Auction.Api.Interceptors;

/// <summary>
/// Turns exceptions into gRPC status + ErrorDetail (R6).
/// DomainException keeps its code. Anything else becomes INTERNAL with no exception text.
/// </summary>
public sealed class ExceptionInterceptor(ILogger<ExceptionInterceptor> log) : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context);
        }
        catch (Exception ex) when (ShouldMap(ex, context))
        {
            throw Map(ex, context);
        }
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(requestStream, context);
        }
        catch (Exception ex) when (ShouldMap(ex, context))
        {
            throw Map(ex, context);
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            await continuation(request, responseStream, context);
        }
        catch (Exception ex) when (ShouldMap(ex, context))
        {
            throw Map(ex, context);
        }
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            await continuation(requestStream, responseStream, context);
        }
        catch (Exception ex) when (ShouldMap(ex, context))
        {
            throw Map(ex, context);
        }
    }

    // Already-built RpcExceptions and client cancels pass through untouched.
    private static bool ShouldMap(Exception ex, ServerCallContext context) =>
        ex is not RpcException
        && !(ex is OperationCanceledException && context.CancellationToken.IsCancellationRequested);

    private RpcException Map(Exception ex, ServerCallContext context)
    {
        var correlationId = context.GetCorrelationId();

        if (ex is DomainException domain)
        {
            log.LogInformation("{Method} failed with {Code}: {Message} (correlation {CorrelationId})",
                context.Method, domain.Code, domain.Message, correlationId);
            return RpcErrors.From(domain, correlationId);
        }

        log.LogError(ex, "{Method} failed with an unhandled error (correlation {CorrelationId})", context.Method, correlationId);
        return RpcErrors.Internal(correlationId);
    }
}

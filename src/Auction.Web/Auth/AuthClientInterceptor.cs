using Auction.Contracts.Errors;
using Auction.Domain.Errors;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Auction.Web.Auth;

/// <summary>
/// Adds "authorization: Bearer ..." to Api calls. If the Api says UNAUTHENTICATED,
/// refreshes the token once and tries again.
/// </summary>
public sealed class AuthClientInterceptor(TokenStore tokens) : Interceptor
{
    // These calls carry no access token (they are how you get one).
    // These calls carry no access token: the auth calls are how you get one, and images are public.
    private static readonly HashSet<string> NoTokenMethods = ["Register", "Login", "RefreshToken", "Logout", "GetImage"];

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        if (NoTokenMethods.Contains(context.Method.Name))
        {
            return continuation(request, context);
        }

        var response = SendWithTokenAsync(request, context, continuation);
        return new AsyncUnaryCall<TResponse>(response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        if (NoTokenMethods.Contains(context.Method.Name))
        {
            return continuation(request, context);
        }

        // Streams (spec 04) get the current token; no retry.
        var token = tokens.GetAccessTokenAsync().GetAwaiter().GetResult();
        return continuation(request, WithToken(context, token));
    }

    // Client streams (UploadImage, spec 02) need the token too; without it the Api answers "please sign in".
    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context, AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        var token = tokens.GetAccessTokenAsync().GetAwaiter().GetResult();
        return continuation(WithToken(context, token));
    }

    private async Task<TResponse> SendWithTokenAsync<TRequest, TResponse>(
        TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        var token = await tokens.GetAccessTokenAsync(ct: context.Options.CancellationToken);
        try
        {
            return await continuation(request, WithToken(context, token)).ResponseAsync;
        }
        catch (RpcException ex) when (token != null && RpcErrors.CodeOf(ex) == ErrorCodes.Unauthenticated)
        {
            var fresh = await tokens.GetAccessTokenAsync(forceRefresh: true, ct: context.Options.CancellationToken);
            if (fresh == null)
            {
                throw;
            }

            return await continuation(request, WithToken(context, fresh)).ResponseAsync;
        }
    }

    private static ClientInterceptorContext<TRequest, TResponse> WithToken<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context, string? token)
        where TRequest : class
        where TResponse : class
    {
        if (token == null)
        {
            return context;
        }

        var headers = new Metadata();
        if (context.Options.Headers != null)
        {
            foreach (var entry in context.Options.Headers)
            {
                if (entry.Key != "authorization")
                {
                    headers.Add(entry);
                }
            }
        }

        headers.Add("authorization", "Bearer " + token);
        return new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, context.Options.WithHeaders(headers));
    }
}

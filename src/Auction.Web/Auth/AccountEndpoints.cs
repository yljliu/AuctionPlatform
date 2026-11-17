using System.Security.Claims;
using Auction.Contracts;
using Grpc.Core;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Auction.Web.Auth;

public static class AccountEndpoints
{
    public const string ClientIpHeader = "x-client-ip";

    /// <summary>Passes the browser's IP to the Api so the login limit is per user.</summary>
    public static Metadata ClientHeaders(HttpContext http) =>
        [new Metadata.Entry(ClientIpHeader, http.Connection.RemoteIpAddress?.ToString() ?? "unknown")];

    /// <summary>Builds the cookie user. The cookie holds no tokens, only the session id.</summary>
    public static ClaimsPrincipal BuildPrincipal(Me me, string sessionId)
    {
        var claims = new List<Claim>
        {
            new("sub", me.UserId),
            new("name", me.Username),
            new(TokenStore.SessionClaim, sessionId),
        };
        foreach (var role in me.Roles)
        {
            claims.Add(new Claim("role", role));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, "name", "role");
        return new ClaimsPrincipal(identity);
    }

    /// <summary>Only allow local paths like "/auctions/1", never another site.</summary>
    public static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\")
            ? returnUrl
            : "/";

    public static void MapAccountEndpoints(this WebApplication app)
    {
        app.MapPost("/account/logout", async (HttpContext http, IAntiforgery antiforgery, ITokenSessionStore sessions, AuthService.AuthServiceClient auth) =>
        {
            await antiforgery.ValidateRequestAsync(http);

            var sessionId = http.User.FindFirst(TokenStore.SessionClaim)?.Value;
            if (sessionId != null)
            {
                var session = await sessions.GetAsync(sessionId);
                if (session != null)
                {
                    try
                    {
                        await auth.LogoutAsync(new LogoutRequest { RefreshToken = session.RefreshToken });
                    }
                    catch (RpcException)
                    {
                        // Logging out locally still works if the Api is down.
                    }
                }

                await sessions.RemoveAsync(sessionId);
            }

            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/");
        });
    }
}

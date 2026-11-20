using System.Security.Claims;
using Auction.Contracts;
using Grpc.Core;
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
    public static ClaimsPrincipal BuildPrincipal(Me me, string sessionId, string? mode = null)
    {
        var claims = new List<Claim>
        {
            new("sub", me.UserId),
            new("name", me.Username),
            new(TokenStore.SessionClaim, sessionId),
            new(Services.Ui.AccountMode.Claim, Services.Ui.AccountMode.Resolve(me.Roles.Contains("Seller"), mode)),
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

    /// <summary>
    /// CSRF guard for the sign-out POST: the browser's Origin (or Referer) must be this site.
    /// The menu's form is interactive, where the antiforgery token can come back empty, so the token is not used.
    /// Behind the proxy the site's host is in X-Forwarded-Host.
    /// </summary>
    public static bool IsSameOrigin(HttpRequest request)
    {
        var source = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(source) || source == "null")
        {
            source = request.Headers.Referer.ToString();
        }

        if (!Uri.TryCreate(source, UriKind.Absolute, out var from))
        {
            return false;
        }

        var host = request.Headers["X-Forwarded-Host"].ToString();
        if (string.IsNullOrEmpty(host))
        {
            host = request.Host.Value ?? "";
        }

        host = host.Split(',')[0].Trim();
        var fromHost = from.IsDefaultPort ? from.Host : $"{from.Host}:{from.Port}";
        return string.Equals(fromHost, host, StringComparison.OrdinalIgnoreCase);
    }

    public static void MapAccountEndpoints(this WebApplication app)
    {
        // Switch between bidder and seller mode. It also refreshes the tokens, so a Seller role an admin just
        // approved is picked up without signing out (design W9).
        app.MapPost("/account/mode", async (HttpContext http, ITokenSessionStore sessions, AuthService.AuthServiceClient auth) =>
        {
            if (!IsSameOrigin(http.Request))
            {
                return Results.BadRequest();
            }

            var form = await http.Request.ReadFormAsync();
            var back = SafeReturnUrl(form["returnUrl"].ToString() is { Length: > 0 } r ? r : "/me/settings");
            var sessionId = http.User.FindFirst(TokenStore.SessionClaim)?.Value;
            var session = sessionId == null ? null : await sessions.GetAsync(sessionId);
            if (sessionId == null || session == null)
            {
                return Results.Redirect("/account/login?returnUrl=" + Uri.EscapeDataString(back));
            }

            RefreshTokenResponse reply;
            try
            {
                reply = await auth.RefreshTokenAsync(new RefreshTokenRequest { RefreshToken = session.RefreshToken });
            }
            catch (RpcException)
            {
                await sessions.RemoveAsync(sessionId);
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.Redirect("/account/login?returnUrl=" + Uri.EscapeDataString(back));
            }

            var fresh = TokenSession.From(reply.Tokens);
            await sessions.SaveAsync(sessionId, fresh);
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                BuildPrincipal(reply.Me, sessionId, form["mode"].ToString()),
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = fresh.RefreshExpiresAt });
            return Results.Redirect(back);
        });

        app.MapPost("/account/logout", async (HttpContext http, ITokenSessionStore sessions, AuthService.AuthServiceClient auth) =>
        {
            if (!IsSameOrigin(http.Request))
            {
                return Results.BadRequest();
            }

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

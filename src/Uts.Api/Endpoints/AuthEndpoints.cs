using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
using Uts.Api.Services;

namespace Uts.Api.Endpoints;

/// <summary>OIDC (Authentik) sign-in flow and the auth-state probe for the SPA.</summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/auth/login", async (OidcService oidc, HttpContext http, string? next) =>
        {
            var redirectAfter = next is not null && next.StartsWith('/') && !next.StartsWith("//")
                ? next
                : "/admin";

            try
            {
                var url = await oidc.CreateSignInRedirectAsync(new Uri(http.Request.BuildBase()), redirectAfter);
                return url is null
                    ? Results.Text(
                        "OIDC is not configured (set OIDC__AUTHORITY, OIDC__CLIENTID, OIDC__CLIENTSECRET).",
                        statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.Redirect(url);
            }
            catch (Exception e)
            {
                return Results.Text($"Could not reach the identity provider: {e.Message}",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapGet("/signin/uts", async (OidcService oidc, HttpContext http, string? code, string? state) =>
        {
            var principal = await oidc.CompleteSignInAsync(code, state, new Uri(http.Request.BuildBase()));
            if (principal is null)
                return Results.Redirect("/auth/login");

            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            return Results.Redirect("/admin");
        });

        app.MapGet("/auth/logout", (HttpContext http) =>
        {
            var props = new AuthenticationProperties { RedirectUri = "/" };
            return Results.SignOut(props, [CookieAuthenticationDefaults.AuthenticationScheme]);
        });

        app.MapGet("/api/auth/state", (HttpContext http) =>
        {
            var c = http.RequestServices.GetRequiredService<IConfiguration>();
            var user = http.User;
            var authenticated = user.Identity?.IsAuthenticated == true;
            var groupsClaim = c["OIDC:GroupsClaim"] ?? "groups";
            var adminGroup = c["Auth:AdminGroup"] ?? "uts-admins";
            var isAdmin = authenticated && user.HasClaim(groupsClaim, adminGroup);

            return Results.Json(new
            {
                authenticated,
                isAdmin,
                name = authenticated
                    ? user.FindFirst("preferred_username")?.Value
                      ?? user.FindFirst("name")?.Value
                      ?? user.FindFirst(ClaimTypes.Name)?.Value
                    : null,
            });
        });
    }
}

file static class Extensions
{
    public static string BuildBase(this HttpRequest req)
    {
        var host = req.Host.HasValue ? req.Host.Value : req.HostStringFallback();
        return $"{req.Scheme}://{host}";
    }

    private static string HostStringFallback(this HttpRequest _)
    {
        throw new InvalidOperationException("Missing Host header.");
    }
}

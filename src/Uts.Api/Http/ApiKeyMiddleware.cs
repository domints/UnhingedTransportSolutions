using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Uts.Api.Db;

namespace Uts.Api.Http;

/// <summary>
/// Resolves `Authorization: Bearer uts_sk_…` API keys into
/// <see cref="HttpContext.User"/>. Manual middleware — the cookie
/// authentication setup is untouched and an existing browser session always
/// wins; the bearer header is only examined when nothing authenticated the
/// request yet. No permission checking happens here, that is the endpoint
/// filter's job (<see cref="AdminFilter"/>).
/// </summary>
/// <remarks>
/// On a miss (unknown / revoked / malformed token) nothing is authenticated
/// and the response stays a generic 401 from the filter — nothing
/// distinguishes unknown from revoked from bad. The failure is flagged via
/// <see cref="ItemKeyAuthFailed"/> so the rate limiter can throttle token
/// guessing from that IP.
/// </remarks>
public sealed class ApiKeyMiddleware(RequestDelegate next)
{
    /// <summary>Also a const in the filter — never clash with the cookie scheme.</summary>
    public const string Scheme = "ApiKey";

    /// <summary>The unambiguous token prefix: currently no other bearer traffic
    /// exists in the app, so uts_sk_ ⇒ API key vs. any future JWT.</summary>
    public const string TokenPrefix = "uts_sk_";

    /// <summary>Set on any key-authenticated request; the admin filter uses
    /// this to keep key principals off OIDC-only endpoints.</summary>
    public const string ItemKeyAuth = "uts:key-auth";

    /// <summary>Set when a uts_sk_ bearer token was presented but did not
    /// resolve; the global rate-limiter partition reads it.</summary>
    public const string ItemKeyAuthFailed = "uts:key-auth-failed";

    public async Task InvokeAsync(HttpContext http, Database db)
    {
        if (http.User.Identity?.IsAuthenticated != true)
        {
            var header = http.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? header["Bearer ".Length..].Trim()
                : null;

            if (token is not null && token.StartsWith(TokenPrefix, StringComparison.Ordinal))
                await ResolveAsync(http, db, token);
        }

        await next(http);
    }

    private static async Task ResolveAsync(HttpContext http, Database db, string presented)
    {
        // SHA-256 hex of the full presented value (salt unnecessary: ~190 bit
        // of entropy makes rainbow tables a non-topic).
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(presented)));

        await using var conn = await db.OpenAsync(http.RequestAborted);
        var key = await conn.QuerySingleOrDefaultAsync<(long Id, string Name, string[] Permissions)>(
            """
            SELECT id, name, permissions
            FROM api_keys
            WHERE key_hash = @hash AND revoked_at IS NULL
            """,
            new { hash });

        if (key.Id == 0)
        {
            // Not a reason to say anything different than for any other failure.
            http.Items[ItemKeyAuthFailed] = true;
            return;
        }

        var claims = new List<Claim>
        {
            new("sub", $"uts-key:{key.Id}"),
            new("name", key.Name),
            new("apikey", "1"),
        };
        claims.AddRange(key.Permissions.Select(p => new Claim("perms", p)));
        http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
        http.Items[ItemKeyAuth] = true;

        // Best-effort: telemetry, never correctness — a dropped update only
        // leaves last_used_at slightly stale.
        _ = TouchLastUsedAsync(db, key.Id);
    }

    private static async Task TouchLastUsedAsync(Database db, long id)
    {
        try
        {
            await using var conn = await db.Source.OpenConnectionAsync();
            await conn.ExecuteAsync("UPDATE api_keys SET last_used_at = now() WHERE id = @id", new { id });
        }
        catch
        {
            // fire-and-forget by design
        }
    }
}

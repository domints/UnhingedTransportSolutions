using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Uts.Api.Db;
using Uts.Api.Http;
using Uts.Api.Services;

namespace Uts.Api.Endpoints;

/// <summary>
/// Key management for the admin panel. Each admin sees, mints and revokes
/// only their own keys (created_by = their Authentik sub). Routes are marked
/// <see cref="AdminKeysOnlyAttribute"/>: the full key is shown exactly once in
/// the mint response, everything stored afterwards is a hash.
/// </summary>
public static class ApiKeyEndpoints
{
    private const int TokenLength = 32; // random chars after the uts_sk_ prefix

    private static readonly char[] Alphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

    public static void MapApiKeys(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/keys").AddEndpointFilter<AdminFilter>();

        // Own keys only — newest first, prefix stands in for the secret.
        g.MapGet("/", [AdminKeysOnly] async (HttpContext http, Database db, CancellationToken ct) =>
        {
            var sub = RequireSub(http);
            await using var conn = await db.OpenAsync(ct);
            var rows = await conn.QueryAsync<ApiKeyRow>(
                """
                SELECT id          AS Id,
                       name        AS Name,
                       key_prefix  AS KeyPrefix,
                       permissions AS Permissions,
                       created_at  AS CreatedAt,
                       last_used_at AS LastUsedAt,
                       revoked_at  AS RevokedAt
                FROM api_keys
                WHERE created_by = @sub
                ORDER BY created_at DESC
                """,
                new { sub });
            return Results.Ok(rows);
        });

        g.MapPost("/", [AdminKeysOnly] async (CreateKeyRequest req, HttpContext http, Database db,
            TelegramNotifier telegram, CancellationToken ct) =>
        {
            var sub = RequireSub(http);
            var name = req.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 64)
                return Bad("Key name is required (at most 64 characters).");

            var perms = req.Permissions is null ? [] : req.Permissions.ToArray();
            // Unknown strings are rejected — keys.* can never be requested and
            // typos must not silently mint a narrower key.
            if (perms.Any(p => !Perm.All.Contains(p)))
                return Bad("Unknown permission name(s); allowed: " + string.Join(", ", Perm.All) + ".");
            if (perms.Length == 0)
                return Bad("Select at least one permission.");

            // Crypto RNG, shown once, stored only as a hash (+ plaintext prefix).
            var token = ApiKeyMiddleware.TokenPrefix + new string(RandomNumberGenerator.GetItems<char>(Alphabet, TokenLength));
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            var prefix = token[..(ApiKeyMiddleware.TokenPrefix.Length + 8)];

            await using var conn = await db.OpenAsync(ct);
            var row = await conn.QuerySingleAsync<(long Id, DateTime CreatedAt)>(
                """
                INSERT INTO api_keys (name, created_by, key_hash, key_prefix, permissions)
                VALUES (@name, @sub, @hash, @prefix, @perms)
                RETURNING id, created_at
                """,
                new { name, sub, hash, prefix, perms });

            _ = telegram.EnqueueAdminAsync(
                $"🔑 API key '{name}' created by {Display(http)}.");

            return Results.Created($"/api/admin/keys", new
            {
                key = token, // the one and only time
                id = row.Id,
                name,
                keyPrefix = prefix,
                permissions = perms,
                createdAt = row.CreatedAt,
            });
        });

        // Idempotent, own keys only.
        g.MapPost("/{id:long}/revoke", [AdminKeysOnly] async (long id, HttpContext http, Database db, CancellationToken ct) =>
        {
            var sub = RequireSub(http);
            await using var conn = await db.OpenAsync(ct);
            var revoked = await conn.ExecuteAsync(
                """
                UPDATE api_keys SET revoked_at = now()
                WHERE id = @id AND created_by = @sub AND revoked_at IS NULL
                """,
                new { id, sub });

            if (revoked == 1)
                return Results.Ok(new { revoked = true });

            // Not revoked now → either someone else's key (no leak: 404) or
            // already revoked, which is exactly what was asked for.
            var own = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM api_keys WHERE id = @id AND created_by = @sub)",
                new { id, sub });
            return own
                ? Results.Ok(new { revoked = true })
                : Results.NotFound(new { error = "Key not found." });
        });
    }

    /// <summary>created_by is the Authentik sub (stable across renames);
    /// the friendly name comes from the already-authenticated session.</summary>
    private static string RequireSub(HttpContext http)
    {
        var sub = http.User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(sub))
            throw new InvalidOperationException("No subject claim on an admin principal.");
        return sub;
    }

    private static string Display(HttpContext http) =>
        http.User.FindFirst("preferred_username")?.Value
        ?? http.User.FindFirst("name")?.Value
        ?? "an admin";

    private static IResult Bad(string message) => Results.Json(new { error = message },
        statusCode: StatusCodes.Status400BadRequest);

    private sealed record ApiKeyRow(
        long Id, string Name, string KeyPrefix, string[] Permissions,
        DateTime CreatedAt, DateTime? LastUsedAt, DateTime? RevokedAt);

    public sealed record CreateKeyRequest(string Name, List<string>? Permissions);
}

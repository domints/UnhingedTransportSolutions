using Dapper;
using System.Security.Claims;
using Uts.Api.Db;
using Uts.Api.Domain;
using Uts.Api.Http;
using Uts.Api.Services;

namespace Uts.Api.Endpoints;

/// <summary>Identity onboarding for couriers. A signed-in uts-admins member
/// whose Authentik 'sub' is not yet mapped to a Client row claims one here:
/// either picks an existing unclaimed client ("I already exist in the
/// address book") or creates a fresh one. Any admin may also detach a claim
/// (e.g. after a mistyped pick) — the affected courier lands back in
/// onboarding on their next login.</summary>
public static class AdminIdentityEndpoints
{
    public static void MapAdminIdentity(this WebApplication app)
    {
        var g = app.MapGroup("/api/admin/identity").AddEndpointFilter<AdminFilter>();

        // Who am I? (sub, claim state — the SPA asks before/after onboarding)
        g.MapGet("/", async (AdminIdentity identity, HttpContext http, CancellationToken ct) =>
        {
            var client = await identity.ResolveAsync(http.User, ct);
            return Results.Ok(new
            {
                sub = http.User.FindFirst("sub")?.Value,
                clientId = client?.Id,
                clientName = client?.Name,
                onboarded = client is not null,
            });
        });

        // Unclaimed client rows — the "pick who you are" list.
        g.MapGet("/unclaimed", async (Database db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var rows = await conn.QueryAsync<Client>(
                """
                SELECT id AS Id, name AS Name, contact AS Contact, telegram_chat_id::text AS TelegramChatId,
                       notes AS Notes, created_at AS CreatedAt
                FROM clients
                WHERE auth_sub IS NULL
                ORDER BY name
                """);
            return Results.Ok(rows);
        });

        // Couriers = clients bound to a login identity. Feeds the trip-form
        // "courier" select — only real mapped humans should appear there.
        // NOTE: project to anonymous objects — System.Text.Json ignores
        // value-tuple fields by default, which would serialize these rows
        // as [{}, {}, …].
        g.MapGet("/couriers", async (Database db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var rows = await conn.QueryAsync<(long Id, string Name, string? Contact)>(
                "SELECT id, name, contact FROM clients WHERE auth_sub IS NOT NULL ORDER BY name");
            return Results.Ok(rows.Select(r => new { id = r.Id, name = r.Name, contact = r.Contact }));
        });

        // Claim an existing (still unclaimed) client row...
        g.MapPost("/claim", async (ClaimRequest req, AdminIdentity identity, Database db, HttpContext http, CancellationToken ct) =>
        {
            var sub = http.User.FindFirst("sub")?.Value;
            if (string.IsNullOrEmpty(sub))
                return Results.Unauthorized();

            await using var conn = await db.OpenAsync(ct);

            // Already mapped? Then the only sane answer is "no, you are already someone".
            var own = await identity.ResolveAsync(conn, http.User);
            if (own is not null)
                return Results.Json(new { error = $"You are already mapped to client #{own.Id} ({own.Name})." },
                    statusCode: StatusCodes.Status409Conflict);

            var id = req.ClientId;
            if (id is not null)
            {
                // Conditional update: claim only sticks if the row is still unclaimed.
                var claimed = await conn.ExecuteAsync(
                    "UPDATE clients SET auth_sub = @sub WHERE id = @id AND auth_sub IS NULL",
                    new { sub, id });
                return claimed == 1
                    ? Results.Ok(new { clientId = id })
                    : Results.Json(new { error = "That client is already claimed (or does not exist)." },
                        statusCode: StatusCodes.Status409Conflict);
            }

            // ...or create a brand-new client row for yourself.
            var name = req.Name?.Trim();
            if (string.IsNullOrEmpty(name))
                return Results.Json(new { error = "Provide a client name (or a clientId to claim)." },
                    statusCode: StatusCodes.Status400BadRequest);

            var created = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO clients (name, telegram_notifications, auth_sub)
                VALUES (@name, TRUE, @sub)
                RETURNING id
                """,
                new { name, sub });
            return Results.Created("/api/admin/identity", new { clientId = created });
        });

        // Detach a login from a client row. Any admin may do this — you cannot
        // lock yourself out of onboarding forever, and a wrong "pick" is fixed
        // in two clicks instead of a psql session.
        g.MapPost("/unclaim", async (UnclaimRequest req, Database db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var freed = await conn.ExecuteAsync(
                "UPDATE clients SET auth_sub = NULL WHERE id = @id AND auth_sub IS NOT NULL",
                new { req.ClientId });
            return freed == 1
                ? Results.Ok(new { freed = true })
                : Results.Json(new { error = "That client has no login to detach (or does not exist)." },
                    statusCode: StatusCodes.Status404NotFound);
        });
    }

    public sealed record ClaimRequest(long? ClientId, string? Name);

    public sealed record UnclaimRequest(long ClientId);
}

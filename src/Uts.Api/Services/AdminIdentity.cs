using System.Security.Claims;
using Dapper;
using Npgsql;
using Uts.Api.Db;

namespace Uts.Api.Services;

/// <summary>Resolves the signed-in admin (Authentik 'sub' claim) to the mapped
/// Client row. Couriers are identified by <c>clients.auth_sub</c>, set once
/// through the /api/admin/identity claim flow. A courier without a mapped
/// client gets funneled to /admin/onboarding.</summary>
public sealed class AdminIdentity(Database db)
{
    public sealed record MappedClient(long Id, string Name);

    /// <summary>Uses an already-open connection (caller may be inside a transaction).</summary>
    public async Task<MappedClient?> ResolveAsync(NpgsqlConnection conn, ClaimsPrincipal? user)
    {
        var sub = user?.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(sub))
            return null;
        return await conn.QuerySingleOrDefaultAsync<MappedClient>(
            "SELECT id AS Id, name AS Name FROM clients WHERE auth_sub = @sub", new { sub });
    }

    /// <summary>Opens its own connection — for request-scoped checks.</summary>
    public async Task<MappedClient?> ResolveAsync(ClaimsPrincipal? user, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await ResolveAsync(conn, user);
    }
}

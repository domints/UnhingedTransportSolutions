using Dapper;

namespace Uts.Api.Db;

/// <summary>
/// Runs embedded, numbered SQL migrations on startup, tracking applied
/// versions in the <c>schema_versions</c> table (DbUp-style, zero deps).
/// </summary>
public static class DbMigrator
{
    private const string Prefix = "Uts.Api.Migrations.";

    public static async Task RunAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<Database>();
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("Uts.Migrations");

        await using var conn = await db.Source.OpenConnectionAsync();

        await conn.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS schema_versions (
                version    TEXT PRIMARY KEY,
                applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
            )
            """);

        var applied = (await conn.QueryAsync<string>("SELECT version FROM schema_versions"))
            .ToHashSet(StringComparer.Ordinal);

        var scripts = typeof(DbMigrator).Assembly
            .GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        foreach (var name in scripts)
        {
            var version = name[Prefix.Length..];
            if (applied.Contains(version))
                continue;

            await using var stream = typeof(DbMigrator).Assembly.GetManifestResourceStream(name)
                                     ?? throw new InvalidOperationException($"Embedded migration '{name}' not found.");
            var sql = await new StreamReader(stream).ReadToEndAsync();

            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync(sql, transaction: tx);
            await conn.ExecuteAsync(
                "INSERT INTO schema_versions (version) VALUES (@version)",
                new { version }, tx);
            await tx.CommitAsync();

            log.LogInformation("Applied migration {Version}", version);
        }
    }
}

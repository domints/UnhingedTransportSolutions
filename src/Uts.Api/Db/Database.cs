using Npgsql;

namespace Uts.Api.Db;

/// <summary>Central access point for the PostgreSQL data source.</summary>
public sealed class Database
{
    public NpgsqlDataSource Source { get; }

    public Database(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Database")
                 ?? throw new InvalidOperationException(
                     "Database connection string missing — set ConnectionStrings__Database.");
        Source = NpgsqlDataSource.Create(cs);
    }

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
        => await Source.OpenConnectionAsync(ct);
}

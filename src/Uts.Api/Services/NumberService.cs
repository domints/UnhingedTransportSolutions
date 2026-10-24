using System.Globalization;
using Dapper;
using Npgsql;

namespace Uts.Api.Services;

/// <summary>
/// Generates tracking numbers: <c>UTS2137</c> + zero-padded sequence + destination city code,
/// e.g. <c>UTS2137000012WRO</c>. The sequence lives in Postgres, so numbers are unique
/// across app restarts and concurrent inserts.
/// </summary>
public sealed class NumberService(IConfiguration cfg)
{
    public string Prefix => cfg["Numbers:Prefix"] ?? "UTS2137";
    public int Digits => cfg.GetValue("Numbers:Digits", 6);

    public async Task<string> NextAsync(NpgsqlConnection conn, string destinationCode)
    {
        var seq = await conn.ExecuteScalarAsync<long>("SELECT nextval('shipment_number_seq')");
        var padded = seq.ToString(new string('0', Digits), CultureInfo.InvariantCulture);
        return $"{Prefix}{padded}{destinationCode.ToUpperInvariant()}";
    }
}

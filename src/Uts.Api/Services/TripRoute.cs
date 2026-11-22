using Dapper;
using Npgsql;

namespace Uts.Api.Services;

/// <summary>
/// Route-matching rules for "can this parcel travel on that trip?". Shared by
/// the single-shipment trip assignment (<c>POST /api/admin/shipments/{id}/trip</c>)
/// and the bulk "load this courier's waiting queue onto the trip" action, so
/// both agree on what a route covers.
/// </summary>
public static class TripRoute
{
    /// <summary>Intermediate stops of a trip, in travel order.</summary>
    public static async Task<string[]> StopsAsync(NpgsqlConnection conn, long tripId)
        => (await conn.QueryAsync<string>(
            "SELECT city_code FROM trip_stops WHERE trip_id = @tripId ORDER BY position",
            new { tripId })).ToArray();

    /// <summary>
    /// A trip covers a parcel when the parcel's destination is the trip's
    /// destination or one of its stops. A trip without a destination (legal,
    /// if unusual) covers everything.
    /// </summary>
    public static bool Covers(string? tripDestination, IReadOnlyCollection<string> stops, string shipmentDestination)
        => tripDestination is null
           || tripDestination == shipmentDestination
           || stops.Contains(shipmentDestination);

    /// <summary>"KRK → KAT → WRO" for error messages.</summary>
    public static string Describe(string? origin, IEnumerable<string> stops, string? destination)
        => $"{origin ?? "?"} → {string.Join(" → ", stops)} → {destination ?? "?"}";
}

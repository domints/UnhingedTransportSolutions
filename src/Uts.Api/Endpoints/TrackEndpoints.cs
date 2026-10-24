using System.Threading.RateLimiting;
using Dapper;
using Microsoft.AspNetCore.RateLimiting;
using Uts.Api.Db;
using Uts.Api.Http;
using Uts.Api.Domain;
using Uts.Api.Services;

namespace Uts.Api.Endpoints;

/// <summary>
/// Public tracking API. Basic status/route/timeline is open; the 6-digit access
/// code unlocks sender, recipient, description and the planned trip. Trip events
/// are hidden from the public timeline until unlocked.
/// </summary>
public static class TrackEndpoints
{
    public static void MapTrackEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/track");

        api.MapGet("/{number}", async (string number, Database db, CancellationToken ct) =>
        {
            var num = Normalize(number);
            await using var conn = await db.OpenAsync(ct);

            var sh = await conn.QuerySingleOrDefaultAsync<TrackRow>(
                """
                SELECT s.id                AS Id,
                       s.tracking_number   AS Number,
                       s.status            AS Status,
                       s.origin_code       AS OriginCode,
                       s.destination_code  AS DestinationCode,
                       c.name              AS DestinationName,
                       s.created_at        AS CreatedAt,
                       s.delivered_at      AS DeliveredAt
                FROM shipments s
                JOIN cities c ON c.code = s.destination_code
                WHERE s.tracking_number = @num
                """,
                new { num });
            if (sh is null)
                return NotFound();

            var events = (await conn.QueryAsync<TrackEvent>(
                """
                SELECT type AS Type, message AS Message, created_at AS CreatedAt
                FROM shipment_events
                WHERE shipment_id = @id AND type <> 'trip'
                ORDER BY created_at DESC
                """,
                new { id = sh.Id })).ToArray();

            return Results.Ok(new TrackPublic(
                sh.Number, sh.Status, sh.OriginCode, sh.DestinationCode, sh.DestinationName,
                sh.CreatedAt, sh.DeliveredAt, UnlockRequired: true, events));
        });

        api.MapPost("/{number}/unlock", async
                (string number, UnlockPasswordRequest req, Database db, PinService pins, CancellationToken ct) =>
        {
            var num = Normalize(number);
            await using var conn = await db.OpenAsync(ct);

            var row = await conn.QuerySingleOrDefaultAsync<TrackUnlockRow>(
                """
                SELECT s.id                 AS Id,
                       s.tracking_number    AS Number,
                       s.status             AS Status,
                       s.origin_code        AS OriginCode,
                       oc.name              AS OriginName,
                       s.destination_code   AS DestinationCode,
                       dc.name              AS DestinationName,
                       s.created_at         AS CreatedAt,
                       s.delivered_at       AS DeliveredAt,
                       sender.name          AS SenderName,
                       recipient.name       AS RecipientName,
                       s.description        AS Description,
                       s.transport_mode     AS TransportMode,
                       s.notes              AS Notes,
                       s.pin_salt           AS PinSalt,
                       s.pin_hash           AS PinHash,
                       t.title              AS TripTitle,
                       t.planned_date       AS TripPlannedDate
                FROM shipments s
                JOIN cities dc ON dc.code = s.destination_code
                LEFT JOIN cities oc ON oc.code = s.origin_code
                JOIN clients sender ON sender.id = s.sender_id
                JOIN clients recipient ON recipient.id = s.recipient_id
                LEFT JOIN trips t ON t.id = s.trip_id
                WHERE s.tracking_number = @num
                """,
                new { num });
            if (row is null)
                return NotFound();

            if (string.IsNullOrWhiteSpace(req.Pin) || !pins.Verify(req.Pin.Trim(), row.PinSalt, row.PinHash))
                return Results.Json(
                    new { error = "Wrong access code." },
                    statusCode: StatusCodes.Status400BadRequest);

            var events = (await conn.QueryAsync<TrackEvent>(
                """
                SELECT type AS Type, message AS Message, created_at AS CreatedAt
                FROM shipment_events
                WHERE shipment_id = @id
                ORDER BY created_at DESC
                """,
                new { id = row.Id })).ToArray();

            return Results.Ok(new TrackFull(
                row.Number, row.Status, row.OriginCode, row.OriginName, row.DestinationCode, row.DestinationName,
                row.CreatedAt, row.DeliveredAt, row.SenderName, row.RecipientName, row.Description,
                row.TransportMode, row.Notes, row.TripTitle, row.TripPlannedDate, UnlockRequired: false, events));
        })
        .RequireRateLimiting("pin-unlock");
    }

    private static string Normalize(string number)
        => number.Replace(" ", "").Trim().ToUpperInvariant();

    private static IResult NotFound()
        => Results.Json(
            new { error = "Tracking number not found. Maybe it is still parked in someone's backpack." },
            statusCode: StatusCodes.Status404NotFound);

    public sealed record UnlockPasswordRequest(string Pin);

    private sealed record TrackRow(
        long Id,
        string Number,
        string Status,
        string? OriginCode,
        string DestinationCode,
        string DestinationName,
        DateTime CreatedAt,
        DateTime? DeliveredAt);

    private sealed record TrackUnlockRow(
        long Id,
        string Number,
        string Status,
        string? OriginCode,
        string? OriginName,
        string DestinationCode,
        string DestinationName,
        DateTime CreatedAt,
        DateTime? DeliveredAt,
        string SenderName,
        string RecipientName,
        string Description,
        string TransportMode,
        string? Notes,
        string PinSalt,
        string PinHash,
        string? TripTitle,
        DateTime? TripPlannedDate);
}

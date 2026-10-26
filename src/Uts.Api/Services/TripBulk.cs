using Dapper;
using Npgsql;
using Uts.Api.Domain;

namespace Uts.Api.Services;

/// <summary>Packlist bulk action shared by the admin panel and the Telegram
/// bot: "leaving" moves the whole haul picked_up → in_transit, "arrived" moves
/// it in_transit → at_destination and completes the trip. Shipments whose
/// current status cannot legally transition are skipped and reported back.
/// Recipient digests are written into the outbox in the same transaction.</summary>
public sealed class TripBulk(TelegramNotifier telegram)
{
    public sealed record BulkResult(string From, string To, List<string> Updated, List<object> Skipped);

    public async Task<BulkResult?> MarkAllAsync(NpgsqlConnection conn, long tripId, string to)
    {
        var from = to == ShipmentStatus.InTransit ? ShipmentStatus.PickedUp : ShipmentStatus.InTransit;

        var trip = await conn.QuerySingleOrDefaultAsync<(string Title, string? DestinationName, string? CourierName)>(
            """
            SELECT t.title, dc.name, cc.name
            FROM trips t
            LEFT JOIN cities dc ON dc.code = t.destination_code
            LEFT JOIN clients cc ON cc.id = t.courier_client_id
            WHERE t.id = @id
            """,
            new { id = tripId });
        if (trip.Title is null)
            return null;

        var assigned = (await conn.QueryAsync<(long Id, string TrackingNumber, string Status, string RecipientName, string? RecipientChat, bool RecipientNotifications)>(
            """
            SELECT s.id, s.tracking_number, s.status, r.name, r.telegram_chat_id::text, r.telegram_notifications
            FROM shipments s
            JOIN clients r ON r.id = s.recipient_id
            WHERE s.trip_id = @id
            """, new { id = tripId })).AsList();

        await using var tx = await conn.BeginTransactionAsync();
        var updated = new List<string>();
        var skipped = new List<object>();
        var notified = new List<(string ChatId, string Number)>();

        foreach (var s in assigned)
        {
            if (s.Status != from || !ShipmentStatus.CanTransition(s.Status, to))
            {
                skipped.Add(new { trackingNumber = s.TrackingNumber, status = s.Status });
                continue;
            }

            await conn.ExecuteAsync(
                """
                UPDATE shipments SET status = @to, updated_at = now() WHERE id = @sid;
                INSERT INTO shipment_events (shipment_id, type, message)
                VALUES (@sid, 'status', @msg);
                """,
                new { sid = s.Id, to, msg = $"Status updated: {ShipmentStatus.Label(to).ToUpperInvariant()}" },
                tx);
            updated.Add(s.TrackingNumber);
            if (s.RecipientChat is not null && s.RecipientNotifications)
                notified.Add((s.RecipientChat, s.TrackingNumber));
        }

        // Arriving marks the journey itself done (only if still planned).
        if (to == ShipmentStatus.AtDestination)
            await conn.ExecuteAsync(
                "UPDATE trips SET status = 'completed' WHERE id = @id AND status = 'planned'",
                new { id = tripId }, tx);

        // One digest per recipient, not one message per parcel.
        if (notified.Count > 0)
        {
            if (to == ShipmentStatus.InTransit)
                await telegram.EnqueueTripAsync(conn, tx, trip.DestinationName,
                    (city, numbers) => TelegramTexts.TripDeparture(trip.CourierName, numbers), notified);
            else
                await telegram.EnqueueTripAsync(conn, tx, trip.DestinationName,
                    (city, numbers) => TelegramTexts.TripArrival(trip.CourierName, city, numbers), notified);
        }

        await tx.CommitAsync();
        return new BulkResult(from, to, updated, skipped);
    }
}

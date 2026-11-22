using Dapper;
using Uts.Api.Db;
using Uts.Api.Http;
using Uts.Api.Domain;
using Uts.Api.Services;

namespace Uts.Api.Endpoints;

/// <summary>Admin shipment management (list, create, status, trip, custody, events, notes, PIN).</summary>
public static class AdminShipmentsEndpoints
{
    public static void MapAdminShipments(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/shipments").AddEndpointFilter<AdminFilter>();

        g.MapGet("/", [AdminPerm(Perm.ShipmentsRead)] async (Database db, string? status, string? city, long? clientId,
            string? tripId, string? courierId, string? q, CancellationToken ct) =>
        {
            var conditions = new List<string>();
            var p = new DynamicParameters();

            if (!string.IsNullOrWhiteSpace(status) && ShipmentStatus.All.Contains(status.Trim()))
            {
                conditions.Add("s.status = @status");
                p.Add("status", status.Trim());
            }
            if (!string.IsNullOrWhiteSpace(city))
            {
                conditions.Add("(s.origin_code = @city OR s.destination_code = @city)");
                p.Add("city", city.Trim().ToUpperInvariant());
            }
            if (clientId is not null)
            {
                conditions.Add("(s.sender_id = @clientId OR s.recipient_id = @clientId)");
                p.Add("clientId", clientId);
            }
            if (!string.IsNullOrWhiteSpace(tripId))
            {
                if (tripId == "none")
                {
                    conditions.Add("s.trip_id IS NULL");
                }
                else if (long.TryParse(tripId, out var tid))
                {
                    conditions.Add("s.trip_id = @tripId");
                    p.Add("tripId", tid);
                }
            }
            // Custody filter: who physically holds the parcel right now.
            if (!string.IsNullOrWhiteSpace(courierId))
            {
                if (courierId == "none")
                {
                    conditions.Add("s.assigned_courier_client_id IS NULL");
                }
                else if (long.TryParse(courierId, out var cuid))
                {
                    conditions.Add("s.assigned_courier_client_id = @courierId");
                    p.Add("courierId", cuid);
                }
            }
            if (!string.IsNullOrWhiteSpace(q))
            {
                conditions.Add("""
                    (s.tracking_number ILIKE @q
                     OR sender.name ILIKE @q
                     OR recipient.name ILIKE @q
                     OR s.description ILIKE @q)
                    """);
                p.Add("q", $"%{q.Trim()}%");
            }

            var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
            var sql = $"""
                SELECT s.id                    AS Id,
                       s.tracking_number       AS TrackingNumber,
                       s.status                AS Status,
                       s.transport_mode        AS TransportMode,
                       s.origin_code           AS OriginCode,
                       s.destination_code      AS DestinationCode,
                       dc.name                 AS DestinationName,
                       sender.name             AS SenderName,
                       recipient.name          AS RecipientName,
                       s.description           AS Description,
                       s.trip_id               AS TripId,
                       t.title                 AS TripTitle,
                       t.planned_date          AS TripPlannedDate,
                       s.assigned_courier_client_id AS AssignedCourierClientId,
                       cust.name               AS AssignedCourierName,
                       s.created_at            AS CreatedAt
                FROM shipments s
                JOIN clients sender ON sender.id = s.sender_id
                JOIN clients recipient ON recipient.id = s.recipient_id
                JOIN cities dc ON dc.code = s.destination_code
                LEFT JOIN trips t ON t.id = s.trip_id
                LEFT JOIN clients cust ON cust.id = s.assigned_courier_client_id
                {where}
                ORDER BY s.created_at DESC
                LIMIT 500
                """;

            await using var conn = await db.OpenAsync(ct);
            var rows = await conn.QueryAsync<ShipmentListItem>(new CommandDefinition(sql, p, cancellationToken: ct));
            return Results.Ok(rows);
        });

        g.MapPost("/", [AdminPerm(Perm.ShipmentsWrite)] async (CreateShipmentRequest req, Database db, NumberService numbers, PinService pins,
            TelegramNotifier telegram, CancellationToken ct) =>
        {
            var description = req.Description?.Trim();
            if (string.IsNullOrEmpty(description))
                return Bad("Description is required.");

            var origin = req.OriginCode?.Trim().ToUpperInvariant() ?? "";
            var destination = req.DestinationCode?.Trim().ToUpperInvariant() ?? "";
            var mode = string.IsNullOrWhiteSpace(req.TransportMode) ? "unknown" : req.TransportMode.Trim();
            if (mode is not ("train" or "car" or "unknown"))
                return Bad("Transport mode must be 'train', 'car' or 'unknown'.");
            if (origin.Length == 3 && origin == destination)
                return Bad("Origin and destination must differ.");

            await using var conn = await db.OpenAsync(ct);

            if (!await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM cities WHERE code = @c AND active)", new { c = origin }))
                return Bad($"Origin city '{origin}' is unknown or inactive.");
            if (!await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM cities WHERE code = @c AND active)", new { c = destination }))
                return Bad($"Destination city '{destination}' is unknown or inactive.");
            if (!await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM clients WHERE id = @id)", new { id = req.SenderId }))
                return Bad("Sender not found.");
            if (!await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM clients WHERE id = @id)", new { id = req.RecipientId }))
                return Bad("Recipient not found.");

            var pin = pins.Generate();

            await using var tx = await conn.BeginTransactionAsync();
            var number = await numbers.NextAsync(conn, destination);
            var created = await conn.QuerySingleAsync<(long Id, DateTime CreatedAt)>(
                """
                INSERT INTO shipments
                    (tracking_number, sender_id, recipient_id, origin_code, destination_code,
                     description, transport_mode, status, pin, notes)
                VALUES
                    (@number, @senderId, @recipientId, @origin, @destination,
                     @description, @mode, 'registered', @pin, @notes)
                RETURNING id, created_at
                """,
                new
                {
                    number,
                    senderId = req.SenderId,
                    recipientId = req.RecipientId,
                    origin,
                    destination,
                    description,
                    mode,
                    pin,
                    notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim(),
                },
                tx);
            await conn.ExecuteAsync(
                "INSERT INTO shipment_events (shipment_id, type, message) VALUES (@id, 'created', @msg)",
                new
                {
                    id = created.Id,
                    // no shipment contents here — this event shows on the public tracking page
                    msg = $"Shipment registered: {origin} → {destination}",
                }, tx);

            //Recipient push (out-of-session, still transactional) + courier alert.
            await telegram.EnqueueShipmentStatusAsync(conn, tx, created.Id, "registered");
            await tx.CommitAsync();
            // No courier alert here on purpose: whoever creates the shipment
            // already sees the confirmation in the panel, and the recipient
            // push (EnqueueShipmentStatusAsync, "registered") carries the news.

            var detail = await LoadDetail(conn, created.Id, ct);
            return Results.Created($"/api/admin/shipments/{created.Id}", new { pin, shipment = detail });
        });

        g.MapGet("/{id:long}", [AdminPerm(Perm.ShipmentsRead)] async (long id, Database db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var detail = await LoadDetail(conn, id, ct);
            return detail is null ? Results.NotFound(new { error = "Shipment not found." }) : Results.Ok(detail);
        });

        g.MapPost("/{id:long}/status", [AdminPerm(Perm.ShipmentsWrite)] async (long id, StatusChangeRequest req, Database db, TelegramNotifier telegram, CancellationToken ct) =>
        {
            var to = req.Status?.Trim() ?? "";
            if (!ShipmentStatus.All.Contains(to))
                return Bad($"Unknown status '{to}'.");

            await using var conn = await db.OpenAsync(ct);
            var current = await conn.QuerySingleOrDefaultAsync<(string Status, string DestinationCode)>(
                "SELECT status, destination_code FROM shipments WHERE id = @id", new { id });
            if (current.Status is null)
                return Results.NotFound(new { error = "Shipment not found." });

            if (!ShipmentStatus.CanTransition(current.Status, to))
                return Results.Json(
                    new { error = $"Cannot move from '{ShipmentStatus.Label(current.Status)}' to '{ShipmentStatus.Label(to)}'." },
                    statusCode: StatusCodes.Status409Conflict);

            var backward = ShipmentStatus.IsBackward(current.Status, to);
            var leftDelivered = current.Status == ShipmentStatus.Delivered && to != ShipmentStatus.Delivered;

            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync(
                """
                UPDATE shipments
                SET status = @to,
                    updated_at = now(),
                    -- entering 'delivered' stamps the moment; leaving it (rollback)
                    -- clears it, so the tracking page stops claiming a delivery.
                    delivered_at = CASE WHEN @to = 'delivered' THEN now()
                                        WHEN @leftDelivered THEN NULL
                                        ELSE delivered_at END
                WHERE id = @id
                """,
                new { to, leftDelivered, id }, tx);
            await conn.ExecuteAsync(
                "INSERT INTO shipment_events (shipment_id, type, message) VALUES (@id, 'status', @msg)",
                new
                {
                    id,
                    msg = backward
                        ? $"Status reverted: {ShipmentStatus.Label(current.Status).ToUpperInvariant()} → {ShipmentStatus.Label(to).ToUpperInvariant()}"
                        : $"Status updated: {ShipmentStatus.Label(to).ToUpperInvariant()}",
                }, tx);
            // Recipients hear about every forward move; a rollback is an internal
            // correction — only leaving 'delivered' is worth a (corrective) push.
            if (!backward)
                await telegram.EnqueueShipmentStatusAsync(conn, tx, id, to);
            else if (leftDelivered)
                await telegram.EnqueueStatusCorrectionAsync(conn, tx, id, to);
            await tx.CommitAsync();

            var detail = await LoadDetail(conn, id, ct);
            return Results.Ok(detail);
        });

        g.MapPost("/{id:long}/trip", [AdminPerm(Perm.ShipmentsWrite)] async (long id, TripAssignRequest req, Database db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);

            var current = await conn.QuerySingleOrDefaultAsync<(long? TripId, string DestinationCode)>(
                "SELECT trip_id, destination_code FROM shipments WHERE id = @id", new { id });
            if (current.TripId is null && req.TripId is null)
                return Bad("Shipment is not assigned to any trip.");
            if (current.DestinationCode is null)
                return Results.NotFound(new { error = "Shipment not found." });

            if (req.TripId is null)
            {
                var old = await conn.QuerySingleOrDefaultAsync<string?>(
                    "SELECT t.title FROM trips t WHERE t.id = @t", new { t = current.TripId });
                await conn.ExecuteAsync("UPDATE shipments SET trip_id = NULL, updated_at = now() WHERE id = @id", new { id });
                if (old is not null)
                    await conn.ExecuteAsync(
                        "INSERT INTO shipment_events (shipment_id, type, message) VALUES (@id, 'trip', @msg)",
                        new { id, msg = $"Removed from trip \"{old}\"." });
                var removed = await LoadDetail(conn, id, ct);
                return Results.Ok(removed);
            }

            var tripId = req.TripId!.Value;
            var trip = await conn.QuerySingleOrDefaultAsync<(string Title, DateTime? PlannedDate, string Status, string? OriginCode, string? DestinationCode, long? CourierClientId)>(
                "SELECT title, planned_date, status, origin_code, destination_code, courier_client_id FROM trips WHERE id = @tid",
                new { tid = tripId });
            if (trip.Title is null)
                return Results.NotFound(new { error = "Trip not found." });
            if (trip.Status != "planned")
                return Results.Json(
                    new { error = $"Trip '{trip.Title}' is {trip.Status} — shipments can only join planned trips." },
                    statusCode: StatusCodes.Status409Conflict);
            var route = await TripRoute.StopsAsync(conn, tripId);
            if (!TripRoute.Covers(trip.DestinationCode, route, current.DestinationCode))
                return Results.Json(
                    new
                    {
                        error = $"Trip route ({TripRoute.Describe(trip.OriginCode, route, trip.DestinationCode)}) " +
                                $"does not include shipment destination ({current.DestinationCode})."
                    },
                    statusCode: StatusCodes.Status409Conflict);

            var when = trip.PlannedDate is { } d ? $" (planned {d:yyyy-MM-dd})" : "";
            var title = trip.Title;
            // Joining a trip also puts the parcel in the trip courier's hands —
            // unless the trip names nobody, in which case custody is left alone.
            await conn.ExecuteAsync(
                """
                UPDATE shipments
                SET trip_id = @tid,
                    assigned_courier_client_id = COALESCE(@courier, assigned_courier_client_id),
                    updated_at = now()
                WHERE id = @id
                """,
                new { tid = tripId, courier = trip.CourierClientId, id });
            await conn.ExecuteAsync(
                "INSERT INTO shipment_events (shipment_id, type, message) VALUES (@id, 'trip', @msg)",
                new { id, msg = $"Added to trip \"{title}\"{when}." });

            var detail = await LoadDetail(conn, id, ct);
            return Results.Ok(detail);
        });

        // Custody handover: who physically holds the parcel right now. Deliberately
        // independent of trips — handing a parcel to a courier who has no planned
        // trip yet is the whole point. Optionally detaches it from the current trip
        // and/or steps the status one back (the parcel may have "arrived" with the
        // previous courier, but the last leg starts over with the new one).
        g.MapPost("/{id:long}/courier", [AdminPerm(Perm.ShipmentsWrite)] async (long id, CourierHandoverRequest req, Database db,
            TelegramNotifier telegram, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var current = await conn.QuerySingleOrDefaultAsync<CustodyRow>(
                """
                SELECT s.tracking_number             AS Number,
                       s.trip_id                     AS TripId,
                       s.status                      AS Status,
                       s.destination_code            AS DestinationCode,
                       s.assigned_courier_client_id  AS CourierClientId,
                       cust.name                     AS CourierName
                FROM shipments s
                LEFT JOIN clients cust ON cust.id = s.assigned_courier_client_id
                WHERE s.id = @id
                """, new { id });
            if (current is null)
                return Results.NotFound(new { error = "Shipment not found." });

            if (current.Status is ShipmentStatus.Delivered or ShipmentStatus.Cancelled or ShipmentStatus.Lost)
                return Results.Json(
                    new { error = $"Cannot hand over a {ShipmentStatus.Label(current.Status!).ToLowerInvariant()} shipment. Revert its status first." },
                    statusCode: StatusCodes.Status409Conflict);

            string? newCourierName = null;
            if (req.CourierClientId is { } cid)
            {
                newCourierName = await conn.QuerySingleOrDefaultAsync<string>(
                    "SELECT name FROM clients WHERE id = @cid", new { cid });
                if (newCourierName is null)
                    return Bad("Courier client does not exist.");
            }

            var detach = req.ReleaseFromTrip == true && current.TripId is not null;
            var backStatus = req.RevertStatus == true ? ShipmentStatus.PreviousStatus(current.Status!) : null;
            var custodyChanged = req.CourierClientId != current.CourierClientId;
            var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
            if (!detach && backStatus is null && !custodyChanged && note is null)
                return Bad("Nothing to change — the shipment is already in that courier's custody.");

            await using var tx = await conn.BeginTransactionAsync(ct);

            // 1. Detach from the trip (custody stays where it is; handover is explicit).
            if (detach)
            {
                var tripTitle = await conn.QuerySingleOrDefaultAsync<string?>(
                    "SELECT title FROM trips WHERE id = @tid", new { tid = current.TripId }, tx);
                await conn.ExecuteAsync(
                    "UPDATE shipments SET trip_id = NULL, updated_at = now() WHERE id = @id", new { id }, tx);
                await conn.ExecuteAsync(
                    "INSERT INTO shipment_events (shipment_id, type, message) VALUES (@id, 'trip', @msg)",
                    new { id, msg = tripTitle is null ? "Removed from trip (handover)." : $"Removed from trip \"{tripTitle}\" (handover)." }, tx);
            }

            // 2. Optional one-step status rollback.
            if (backStatus is not null)
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE shipments
                    SET status = @back,
                        updated_at = now(),
                        delivered_at = CASE WHEN @from = 'delivered' THEN NULL ELSE delivered_at END
                    WHERE id = @id
                    """,
                    new { back = backStatus, from = current.Status, id }, tx);
                await conn.ExecuteAsync(
                    "INSERT INTO shipment_events (shipment_id, type, message) VALUES (@id, 'status', @msg)",
                    new
                    {
                        id,
                        msg = $"Status reverted: {ShipmentStatus.Label(current.Status!).ToUpperInvariant()} → {ShipmentStatus.Label(backStatus).ToUpperInvariant()}",
                    }, tx);
            }

            // 3. Custody itself (+ the timeline entry and both pushes).
            if (custodyChanged)
            {
                await conn.ExecuteAsync(
                    "UPDATE shipments SET assigned_courier_client_id = @cid, updated_at = now() WHERE id = @id",
                    new { cid = req.CourierClientId, id }, tx);

                var msg = req.CourierClientId is null
                    ? (current.CourierName is null ? "Custody cleared." : $"Custody cleared (was {current.CourierName}).")
                    : (current.CourierName is null ? $"Custody assigned to {newCourierName}." : $"Custody handed over to {newCourierName}.");
                await conn.ExecuteAsync(
                    "INSERT INTO shipment_events (shipment_id, type, message) VALUES (@id, 'courier', @msg)",
                    new { id, msg = note is null ? msg : $"{msg} {note}" }, tx);

                if (req.CourierClientId is { } custodian)
                    await telegram.EnqueueCourierHandoverAsync(conn, tx, id, custodian);
                // The recipient only cares when someone new now carries their parcel.
                if (newCourierName is not null)
                    await telegram.EnqueueHandoverRecipientAsync(conn, tx, id, newCourierName);
            }
            else if (note is not null)
            {
                await conn.ExecuteAsync(
                    "INSERT INTO shipment_events (shipment_id, type, message) VALUES (@id, 'courier', @msg)",
                    new { id, msg = note }, tx);
            }

            await tx.CommitAsync(ct);

            var detail = await LoadDetail(conn, id, ct);
            return Results.Ok(detail);
        });

        g.MapPost("/{id:long}/events", [AdminPerm(Perm.ShipmentsWrite)] async (long id, ManualEventRequest req, Database db, CancellationToken ct) =>
        {
            var message = req.Message?.Trim();
            if (string.IsNullOrEmpty(message))
                return Bad("Event message is required.");

            await using var conn = await db.OpenAsync(ct);
            if (!await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM shipments WHERE id = @id)", new { id }))
                return Results.NotFound(new { error = "Shipment not found." });

            await conn.ExecuteAsync(
                "INSERT INTO shipment_events (shipment_id, type, message, source) VALUES (@id, 'note', @msg, 'manual')",
                new { id, msg = message });
            var detail = await LoadDetail(conn, id, ct);
            return Results.Ok(detail);
        });

        g.MapPost("/{id:long}/notes", [AdminPerm(Perm.ShipmentsWrite)] async (long id, UpdateNotesRequest req, Database db, TelegramNotifier telegram, CancellationToken ct) =>
        {
            var notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();

            await using var conn = await db.OpenAsync(ct);
            var existing = await conn.QuerySingleOrDefaultAsync<(string? Notes, string Number)>(
                "SELECT notes, tracking_number FROM shipments WHERE id = @id", new { id });
            if (existing.Number is null)
                return Results.NotFound(new { error = "Shipment not found." });

            if (existing.Notes != notes)
            {
                await using var tx = await conn.BeginTransactionAsync(ct);
                await conn.ExecuteAsync(
                    "UPDATE shipments SET notes = @notes, updated_at = now() WHERE id = @id",
                    new { notes, id }, tx);

                // Same event type the manual events use; source 'auto' marks it
                // as a system-generated entry (the timeline dot styling is by type).
                await conn.ExecuteAsync(
                    "INSERT INTO shipment_events (shipment_id, type, source, message) VALUES (@id, 'note', 'auto', @msg)",
                    new { id, msg = notes is null ? "Internal note cleared." : "Internal note updated." }, tx);

                await telegram.EnqueueNotesUpdatedAsync(conn, tx, id, notes);
                await tx.CommitAsync(ct);
            }

            return Results.Ok(await LoadDetail(conn, id, ct));
        });

        g.MapPost("/{id:long}/pin", [AdminPerm(Perm.ShipmentsWrite)] async (long id, Database db, PinService pins, TelegramNotifier telegram, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            if (!await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM shipments WHERE id = @id)", new { id }))
                return Results.NotFound(new { error = "Shipment not found." });

            var pin = pins.Generate();
            await conn.ExecuteAsync(
                "UPDATE shipments SET pin = @pin, updated_at = now() WHERE id = @id",
                new { pin, id });
            var number = await conn.ExecuteScalarAsync<string>(
                "SELECT tracking_number FROM shipments WHERE id = @id", new { id });
            _ = telegram.EnqueueAdminAsync($"🔑 Access code regenerated for {number}.");
            return Results.Ok(new { pin });
        });
    }

    private static async Task<ShipmentDetail?> LoadDetail(Npgsql.NpgsqlConnection conn, long id, CancellationToken ct)
    {
        var s = await conn.QuerySingleOrDefaultAsync<DetailRow>(
            """
            SELECT s.id                 AS Id,
                   s.tracking_number    AS TrackingNumber,
                   s.status             AS Status,
                   s.sender_id          AS SenderId,
                   sender.name          AS SenderName,
                   s.recipient_id       AS RecipientId,
                   recipient.name       AS RecipientName,
                   s.origin_code        AS OriginCode,
                   oc.name              AS OriginName,
                   s.destination_code   AS DestinationCode,
                   dc.name              AS DestinationName,
                   s.description        AS Description,
                   s.transport_mode     AS TransportMode,
                   s.notes              AS Notes,
                   s.pin                AS Pin,
                   s.trip_id            AS TripId,
                   t.title              AS TripTitle,
                   t.planned_date       AS TripPlannedDate,
                   t.status             AS TripStatus,
                   s.assigned_courier_client_id AS AssignedCourierClientId,
                   cust.name            AS AssignedCourierName,
                   s.created_at         AS CreatedAt,
                   s.updated_at         AS UpdatedAt,
                   s.delivered_at       AS DeliveredAt
            FROM shipments s
            JOIN clients sender ON sender.id = s.sender_id
            JOIN clients recipient ON recipient.id = s.recipient_id
            JOIN cities dc ON dc.code = s.destination_code
            LEFT JOIN cities oc ON oc.code = s.origin_code
            LEFT JOIN trips t ON t.id = s.trip_id
            LEFT JOIN clients cust ON cust.id = s.assigned_courier_client_id
            WHERE s.id = @id
            """, new { id });

        if (s is null)
            return null;

        var events = (await conn.QueryAsync<ShipmentEvent>(
            """
            SELECT id             AS Id,
                   shipment_id    AS ShipmentId,
                   type           AS Type,
                   message        AS Message,
                   source         AS Source,
                   created_at     AS CreatedAt
            FROM shipment_events
            WHERE shipment_id = @id
            ORDER BY created_at DESC, id DESC
            """, new { id })).ToArray();

        return new ShipmentDetail(
            s.Id, s.TrackingNumber, s.Status, s.SenderId, s.SenderName, s.RecipientId, s.RecipientName,
            s.OriginCode, s.OriginName, s.DestinationCode, s.DestinationName, s.Description, s.TransportMode,
            s.Notes, s.Pin, s.TripId, s.TripTitle, s.TripPlannedDate, s.TripStatus,
            s.AssignedCourierClientId, s.AssignedCourierName,
            s.CreatedAt, s.UpdatedAt, s.DeliveredAt, events);
    }

    private static IResult Bad(string message) => Results.Json(new { error = message },
        statusCode: StatusCodes.Status400BadRequest);

    public sealed record CreateShipmentRequest(
        long SenderId, long RecipientId, string OriginCode, string DestinationCode,
        string Description, string TransportMode, string? Notes);

    public sealed record StatusChangeRequest(string Status);

    public sealed record TripAssignRequest(long? TripId);

    /// <summary>Custody handover. <c>CourierClientId: null</c> clears the holder;
    /// <c>ReleaseFromTrip</c> detaches from the current trip; <c>RevertStatus</c>
    /// steps the status one back (e.g. in_transit → picked_up mid-route).</summary>
    public sealed record CourierHandoverRequest(
        long? CourierClientId, bool? ReleaseFromTrip, bool? RevertStatus, string? Note);

    public sealed record ManualEventRequest(string Message);

    public sealed record UpdateNotesRequest(string? Notes);

    private sealed record DetailRow(
        long Id,
        string TrackingNumber,
        string Status,
        long SenderId,
        string SenderName,
        long RecipientId,
        string RecipientName,
        string? OriginCode,
        string? OriginName,
        string DestinationCode,
        string DestinationName,
        string Description,
        string TransportMode,
        string? Notes,
        string Pin,
        long? TripId,
        string? TripTitle,
        DateTime? TripPlannedDate,
        string? TripStatus,
        long? AssignedCourierClientId,
        string? AssignedCourierName,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        DateTime? DeliveredAt);

    /// <summary>Custody lookup for the handover endpoint (joined holder name).</summary>
    private sealed record CustodyRow(
        string? Number,
        long? TripId,
        string? Status,
        string? DestinationCode,
        long? CourierClientId,
        string? CourierName);
}

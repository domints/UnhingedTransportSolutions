using Dapper;
using Uts.Api.Db;
using Uts.Api.Http;
using Uts.Api.Domain;
using Uts.Api.Services;

namespace Uts.Api.Endpoints;

/// <summary>Admin shipment management (list, create, status, trip, events, PIN).</summary>
public static class AdminShipmentsEndpoints
{
    public static void MapAdminShipments(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/shipments").AddEndpointFilter<AdminFilter>();

        g.MapGet("/", async (Database db, string? status, string? city, long? clientId, string? tripId, string? q,
            CancellationToken ct) =>
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
                       s.created_at            AS CreatedAt
                FROM shipments s
                JOIN clients sender ON sender.id = s.sender_id
                JOIN clients recipient ON recipient.id = s.recipient_id
                JOIN cities dc ON dc.code = s.destination_code
                LEFT JOIN trips t ON t.id = s.trip_id
                {where}
                ORDER BY s.created_at DESC
                LIMIT 500
                """;

            await using var conn = await db.OpenAsync(ct);
            var rows = await conn.QueryAsync<ShipmentListItem>(new CommandDefinition(sql, p, cancellationToken: ct));
            return Results.Ok(rows);
        });

        g.MapPost("/", async (CreateShipmentRequest req, Database db, NumberService numbers, PinService pins,
            CancellationToken ct) =>
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
                    msg = $"Shipment registered: {origin} → {destination} • {Truncate(description, 80)}",
                }, tx);
            await tx.CommitAsync();

            var detail = await LoadDetail(conn, created.Id, ct);
            return Results.Created($"/api/admin/shipments/{created.Id}", new { pin, shipment = detail });
        });

        g.MapGet("/{id:long}", async (long id, Database db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var detail = await LoadDetail(conn, id, ct);
            return detail is null ? Results.NotFound(new { error = "Shipment not found." }) : Results.Ok(detail);
        });

        g.MapPost("/{id:long}/status", async (long id, StatusChangeRequest req, Database db, CancellationToken ct) =>
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

            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync(
                """
                UPDATE shipments
                SET status = @to,
                    updated_at = now(),
                    delivered_at = CASE WHEN @to = 'delivered' THEN now() ELSE delivered_at END
                WHERE id = @id
                """,
                new { to, id }, tx);
            await conn.ExecuteAsync(
                "INSERT INTO shipment_events (shipment_id, type, message) VALUES (@id, 'status', @msg)",
                new { id, msg = $"Status updated: {ShipmentStatus.Label(to).ToUpperInvariant()}" }, tx);
            await tx.CommitAsync();

            var detail = await LoadDetail(conn, id, ct);
            return Results.Ok(detail);
        });

        g.MapPost("/{id:long}/trip", async (long id, TripAssignRequest req, Database db, CancellationToken ct) =>
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

            var trip = await conn.QuerySingleOrDefaultAsync<(string Title, DateTime? PlannedDate, string Status, string? DestinationCode)>(
                "SELECT title, planned_date, status, destination_code FROM trips WHERE id = @tid",
                new { tid = req.TripId });
            if (trip.Title is null)
                return Results.NotFound(new { error = "Trip not found." });
            if (trip.Status != "planned")
                return Results.Json(
                    new { error = $"Trip '{trip.Title}' is {trip.Status} — shipments can only join planned trips." },
                    statusCode: StatusCodes.Status409Conflict);
            if (trip.DestinationCode is not null && trip.DestinationCode != current.DestinationCode)
                return Results.Json(
                    new { error = $"Trip destination ({trip.DestinationCode}) does not match shipment destination ({current.DestinationCode})." },
                    statusCode: StatusCodes.Status409Conflict);

            var when = trip.PlannedDate is { } d ? $" (planned {d:yyyy-MM-dd})" : "";
            var title = trip.Title;
            await conn.ExecuteAsync(
                "UPDATE shipments SET trip_id = @tid, updated_at = now() WHERE id = @id",
                new { tid = req.TripId, id });
            await conn.ExecuteAsync(
                "INSERT INTO shipment_events (shipment_id, type, message) VALUES (@id, 'trip', @msg)",
                new { id, msg = $"Added to trip \"{title}\"{when}." });

            var detail = await LoadDetail(conn, id, ct);
            return Results.Ok(detail);
        });

        g.MapPost("/{id:long}/events", async (long id, ManualEventRequest req, Database db, CancellationToken ct) =>
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

        g.MapPost("/{id:long}/pin", async (long id, Database db, PinService pins, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            if (!await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM shipments WHERE id = @id)", new { id }))
                return Results.NotFound(new { error = "Shipment not found." });

            var pin = pins.Generate();
            await conn.ExecuteAsync(
                "UPDATE shipments SET pin = @pin, updated_at = now() WHERE id = @id",
                new { pin, id });
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
                   s.trip_id            AS TripId,
                   t.title              AS TripTitle,
                   t.planned_date       AS TripPlannedDate,
                   t.status             AS TripStatus,
                   s.created_at         AS CreatedAt,
                   s.updated_at         AS UpdatedAt,
                   s.delivered_at       AS DeliveredAt
            FROM shipments s
            JOIN clients sender ON sender.id = s.sender_id
            JOIN clients recipient ON recipient.id = s.recipient_id
            JOIN cities dc ON dc.code = s.destination_code
            LEFT JOIN cities oc ON oc.code = s.origin_code
            LEFT JOIN trips t ON t.id = s.trip_id
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
            s.Notes, s.TripId, s.TripTitle, s.TripPlannedDate, s.TripStatus, s.CreatedAt, s.UpdatedAt,
            s.DeliveredAt, events);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static IResult Bad(string message) => Results.Json(new { error = message },
        statusCode: StatusCodes.Status400BadRequest);

    public sealed record CreateShipmentRequest(
        long SenderId, long RecipientId, string OriginCode, string DestinationCode,
        string Description, string TransportMode, string? Notes);

    public sealed record StatusChangeRequest(string Status);

    public sealed record TripAssignRequest(long? TripId);

    public sealed record ManualEventRequest(string Message);

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
        long? TripId,
        string? TripTitle,
        DateTime? TripPlannedDate,
        string? TripStatus,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        DateTime? DeliveredAt);
}

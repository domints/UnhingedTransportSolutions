using Dapper;
using Uts.Api.Db;
using Uts.Api.Http;
using Uts.Api.Domain;

namespace Uts.Api.Endpoints;

/// <summary>Admin CRUD for the supporting entities: clients, cities and trips.</summary>
public static class AdminCatalogEndpoints
{
    public static void MapAdminCatalog(this IEndpointRouteBuilder app)
    {
        MapClients(app);
        MapCities(app);
        MapTrips(app);
    }

    private static void MapClients(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/clients").AddEndpointFilter<AdminFilter>();

        g.MapGet("/", async (Database db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var rows = await conn.QueryAsync<Client>(
                """
                SELECT id              AS Id,
                       name            AS Name,
                       contact         AS Contact,
                       telegram_chat_id AS TelegramChatId,
                       notes           AS Notes,
                       created_at      AS CreatedAt
                FROM clients
                ORDER BY name
                """);
            return Results.Ok(rows);
        });

        g.MapPost("/", async (UpsertClientRequest req, Database db, CancellationToken ct) =>
        {
            var name = req.Name?.Trim();
            if (string.IsNullOrEmpty(name))
                return Bad("Client name is required.");

            await using var conn = await db.OpenAsync(ct);
            var row = await conn.QuerySingleAsync<Client>(
                """
                INSERT INTO clients (name, contact, telegram_chat_id, notes)
                VALUES (@name, @contact, @telegram, @notes)
                RETURNING id, name, contact, telegram_chat_id AS TelegramChatId, notes, created_at AS CreatedAt
                """,
                new
                {
                    name,
                    contact = NullIfEmpty(req.Contact),
                    telegram = NullIfEmpty(req.TelegramChatId),
                    notes = NullIfEmpty(req.Notes),
                });
            return Results.Created($"/api/admin/clients/{row.Id}", row);
        });

        g.MapPut("/{id:long}", async (long id, UpsertClientRequest req, Database db, CancellationToken ct) =>
        {
            var name = req.Name?.Trim();
            if (string.IsNullOrEmpty(name))
                return Bad("Client name is required.");

            await using var conn = await db.OpenAsync(ct);
            var row = await conn.QuerySingleOrDefaultAsync<Client>(
                """
                UPDATE clients
                SET name = @name, contact = @contact, telegram_chat_id = @telegram, notes = @notes
                WHERE id = @id
                RETURNING id, name, contact, telegram_chat_id AS TelegramChatId, notes, created_at AS CreatedAt
                """,
                new
                {
                    id,
                    name,
                    contact = NullIfEmpty(req.Contact),
                    telegram = NullIfEmpty(req.TelegramChatId),
                    notes = NullIfEmpty(req.Notes),
                });
            return row is null ? Results.NotFound(new { error = "Client not found." }) : Results.Ok(row);
        });
    }

    private static void MapCities(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/cities").AddEndpointFilter<AdminFilter>();

        g.MapGet("/", async (Database db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var rows = await conn.QueryAsync<City>(
                "SELECT code AS Code, name AS Name, active AS Active FROM cities ORDER BY code");
            return Results.Ok(rows);
        });

        g.MapPost("/", async (CreateCityRequest req, Database db, CancellationToken ct) =>
        {
            var code = req.Code?.Trim().ToUpperInvariant() ?? "";
            var name = req.Name?.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z]{3}$"))
                return Bad("City code must be exactly 3 letters (A–Z).");
            if (string.IsNullOrEmpty(name))
                return Bad("City name is required.");

            await using var conn = await db.OpenAsync(ct);
            var row = await conn.QuerySingleOrDefaultAsync<City>(
                """
                INSERT INTO cities (code, name) VALUES (@code, @name)
                RETURNING code AS Code, name AS Name, active AS Active
                """,
                new { code, name });

            return row is null
                ? Results.Json(new { error = $"City code '{code}' already exists." },
                    statusCode: StatusCodes.Status409Conflict)
                : Results.Created($"/api/admin/cities/{code}", row);
        });

        g.MapPut("/{code}", async (string code, UpdateCityRequest req, Database db, CancellationToken ct) =>
        {
            var c = code.Trim().ToUpperInvariant();
            await using var conn = await db.OpenAsync(ct);

            if (req.Name is not null)
            {
                var name = req.Name.Trim();
                if (name.Length == 0)
                    return Bad("City name cannot be empty.");
                await conn.ExecuteAsync("UPDATE cities SET name = @name WHERE code = @c", new { name, c });
            }
            if (req.Active is not null)
                await conn.ExecuteAsync("UPDATE cities SET active = @active WHERE code = @c",
                    new { active = req.Active.Value, c });

            var row = await conn.QuerySingleOrDefaultAsync<City>(
                "SELECT code AS Code, name AS Name, active AS Active FROM cities WHERE code = @c", new { c });
            return row is null
                ? Results.NotFound(new { error = "City not found." })
                : Results.Ok(row);
        });
    }

    private static void MapTrips(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/trips").AddEndpointFilter<AdminFilter>();

        g.MapGet("/", async (Database db, string? status, CancellationToken ct) =>
        {
            var st = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
            var filter = st is not null ? "WHERE t.status = @st" : "";

            await using var conn = await db.OpenAsync(ct);
            var rows = await conn.QueryAsync<TripListItem>(
                $"""
                SELECT t.id               AS Id,
                       t.title            AS Title,
                       t.origin_code      AS OriginCode,
                       t.destination_code AS DestinationCode,
                       t.planned_date     AS PlannedDate,
                       t.status           AS Status,
                       t.notes            AS Notes,
                       t.created_at       AS CreatedAt,
                       (SELECT COUNT(*) FROM shipments s WHERE s.trip_id = t.id) AS ShipmentCount
                FROM trips t
                {filter}
                ORDER BY CASE t.status WHEN 'planned' THEN 0 WHEN 'completed' THEN 1 ELSE 2 END,
                         t.planned_date ASC NULLS LAST,
                         t.created_at DESC
                """,
                new { st });
            return Results.Ok(rows);
        });

        g.MapPost("/", async (UpsertTripRequest req, Database db, CancellationToken ct) =>
        {
            var title = req.Title?.Trim();
            if (string.IsNullOrEmpty(title))
                return Bad("Trip title is required.");

            await using var conn = await db.OpenAsync(ct);
            var row = await conn.QuerySingleAsync<Trip>(
                """
                INSERT INTO trips (title, origin_code, destination_code, planned_date, notes)
                VALUES (@title, @origin, @destination, @planned, @notes)
                RETURNING id, title, origin_code AS OriginCode, destination_code AS DestinationCode,
                          planned_date AS PlannedDate, status, notes, created_at AS CreatedAt
                """,
                new { title, origin = req.OriginCode, destination = req.DestinationCode, planned = req.PlannedDate, notes = NullIfEmpty(req.Notes) });
            return Results.Created($"/api/admin/trips/{row.Id}", row);
        });

        g.MapPut("/{id:long}", async (long id, UpsertTripRequest req, Database db, CancellationToken ct) =>
        {
            if (req.Status is { } status && !new[] { "planned", "completed", "cancelled" }.Contains(status))
                return Bad("Trip status must be 'planned', 'completed' or 'cancelled'.");

            await using var conn = await db.OpenAsync(ct);
            var row = await conn.QuerySingleOrDefaultAsync<Trip>(
                """
                UPDATE trips
                SET title            = COALESCE(@title, title),
                    origin_code      = COALESCE(@origin, origin_code),
                    destination_code = COALESCE(@destination, destination_code),
                    planned_date     = COALESCE(@planned, planned_date),
                    status           = COALESCE(@status, status),
                    notes            = COALESCE(@notes, notes)
                WHERE id = @id
                RETURNING id, title, origin_code AS OriginCode, destination_code AS DestinationCode,
                          planned_date AS PlannedDate, status, notes, created_at AS CreatedAt
                """,
                new
                {
                    id,
                    title = NullIfEmpty(req.Title),
                    origin = req.OriginCode,
                    destination = req.DestinationCode,
                    planned = req.PlannedDate,
                    status = req.Status,
                    notes = req.Notes,
                });

            if (row is null)
                return Results.NotFound(new { error = "Trip not found." });

            // Cancelled trips release their shipments for re-assignment.
            // Completed ones keep them — they stay attached for the arrival
            // history until each parcel is individually delivered.
            if (row.Status == "cancelled")
                await conn.ExecuteAsync(
                    "UPDATE shipments SET trip_id = NULL, updated_at = now() WHERE trip_id = @id", new { id });

            return Results.Ok(row);
        });

        // Packlist bulk action: "leaving" marks everything picked_up → in_transit,
        // "arrived" marks everything in_transit → at_destination. Shipments whose
        // current status cannot legally transition are skipped and reported back.
        g.MapPost("/{id:long}/shipments/status", async (long id, TripBulkStatusRequest req, Database db, CancellationToken ct) =>
        {
            var to = req.Status?.Trim() ?? "";
            if (to is not (ShipmentStatus.InTransit or ShipmentStatus.AtDestination))
                return Bad("Bulk status supports only 'in_transit' and 'at_destination'.");

            var from = to == ShipmentStatus.InTransit ? ShipmentStatus.PickedUp : ShipmentStatus.InTransit;

            await using var conn = await db.OpenAsync(ct);
            var trip = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT title FROM trips WHERE id = @id", new { id });
            if (trip is null)
                return Results.NotFound(new { error = "Trip not found." });

            var assigned = (await conn.QueryAsync<(long Id, string TrackingNumber, string Status)>(
                "SELECT id, tracking_number, status FROM shipments WHERE trip_id = @id", new { id })).AsList();

            await using var tx = await conn.BeginTransactionAsync();
            var updated = new List<string>();
            var skipped = new List<object>();

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
            }

            await tx.CommitAsync();

            // Arriving marks the journey itself done — the trip is completed
            // (only if still planned; never resurrect a cancelled trip). The
            // final deliveries happen per-shipment afterwards.
            if (to == ShipmentStatus.AtDestination)
                await conn.ExecuteAsync(
                    "UPDATE trips SET status = 'completed' WHERE id = @id AND status = 'planned'", new { id });

            return Results.Ok(new { from, to, updated, skipped });
        });
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static IResult Bad(string message) => Results.Json(new { error = message },
        statusCode: StatusCodes.Status400BadRequest);

    public sealed record UpsertClientRequest(string Name, string? Contact, string? TelegramChatId, string? Notes);

    public sealed record CreateCityRequest(string Code, string Name);

    public sealed record UpdateCityRequest(string? Name, bool? Active);

    public sealed record UpsertTripRequest(
        string? Title, string? OriginCode, string? DestinationCode, DateTime? PlannedDate, string? Status, string? Notes);

    public sealed record TripBulkStatusRequest(string? Status);
}

using Dapper;
using Uts.Api.Db;
using Uts.Api.Http;
using Uts.Api.Domain;
using Uts.Api.Services;

namespace Uts.Api.Endpoints;

/// <summary>Admin CRUD for the supporting entities: clients, cities and trips.</summary>
public static class AdminCatalogEndpoints
{
    public static void MapAdminCatalog(this IEndpointRouteBuilder app)
    {
        MapClients(app);
        MapCities(app);
        MapTrips(app);
        MapTripTemplates(app);
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

        // Generates a one-week claim code for this client's Telegram binding:
        // /start <code> in the bot (or the t.me deep link) connects the chat.
        g.MapPost("/{id:long}/claim", async (long id, Database db, BotState bot, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            if (!await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM clients WHERE id = @id)", new { id }))
                return Results.NotFound(new { error = "Client not found." });

            var code = TelegramCodes.NewClaimCode();
            await conn.ExecuteAsync(
                "UPDATE clients SET telegram_claim_code = @code, telegram_claim_expires = now() + INTERVAL '7 days' WHERE id = @id",
                new { id, code });

            var url = bot.BotUsername is null ? null : $"https://t.me/{bot.BotUsername}?start={code}";
            return Results.Ok(new { code, url });
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
            var rows = (await conn.QueryAsync<TripListItem>(
                $"""
                SELECT t.id               AS Id,
                       t.title            AS Title,
                       t.origin_code      AS OriginCode,
                       t.destination_code AS DestinationCode,
                       t.planned_date     AS PlannedDate,
                       t.status           AS Status,
                       t.notes            AS Notes,
                       t.created_at       AS CreatedAt,
                       (SELECT COUNT(*) FROM shipments s WHERE s.trip_id = t.id) AS ShipmentCount,
                       ARRAY[]::text[]    AS Stops,
                       t.transport_mode   AS TransportMode,
                       t.courier_client_id AS CourierClientId,
                       cc.name             AS CourierName
                FROM trips t
                LEFT JOIN clients cc ON cc.id = t.courier_client_id
                {filter}
                ORDER BY CASE t.status WHEN 'planned' THEN 0 WHEN 'completed' THEN 1 ELSE 2 END,
                         t.planned_date ASC NULLS LAST,
                         t.created_at DESC
                """,
                new { st })).AsList();

            if (rows.Count > 0)
            {
                var tripIds = rows.Select(r => r.Id).ToArray();
                var stopRows = await conn.QueryAsync<(long TripId, string CityCode)>(
                    "SELECT trip_id, city_code FROM trip_stops WHERE trip_id = ANY(@ids) ORDER BY position",
                    new { ids = tripIds });
                var stopsByTrip = stopRows
                    .GroupBy(s => s.TripId)
                    .ToDictionary(g => g.Key, g => g.Select(s => s.CityCode).ToArray());

                rows = rows
                    .Select(r => r with { Stops = stopsByTrip.GetValueOrDefault(r.Id, Array.Empty<string>()) })
                    .ToList();
            }

            return Results.Ok(rows);
        });

        g.MapPost("/", async (UpsertTripRequest req, Database db, AdminIdentity identity, HttpContext http, CancellationToken ct) =>
        {
            var title = req.Title?.Trim();
            if (string.IsNullOrEmpty(title))
                return Bad("Trip title is required.");

            await using var conn = await db.OpenAsync(ct);

            // Default courier = whoever creates the trip (no request and no
            // creator mapping => nobody). Explicit 0 = "no courier".
            var creator = await identity.ResolveAsync(conn, http.User);
            var courier = req.CourierClientId is 0 ? null : (req.CourierClientId ?? creator?.Id);
            if (courier is not null && !await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM clients WHERE id = @courier)", new { courier }))
                return Bad("Courier client does not exist.");

            if (await ValidateStops(conn, req.OriginCode, req.DestinationCode, req.Stops) is { } stopErr)
                return Bad(stopErr);
            if (!ModeValid(req.TransportMode))
                return Bad("Transport mode must be 'train', 'car' or 'unknown'.");

            var row = await conn.QuerySingleAsync<Trip>(
                """
                INSERT INTO trips (title, origin_code, destination_code, planned_date, notes, transport_mode, courier_client_id)
                VALUES (@title, @origin, @destination, @planned, @notes, @mode, @courier)
                RETURNING id, title, origin_code AS OriginCode, destination_code AS DestinationCode,
                          planned_date AS PlannedDate, status, notes, created_at AS CreatedAt,
                          transport_mode AS TransportMode, courier_client_id AS CourierClientId,
                          (SELECT name FROM clients WHERE id = trips.courier_client_id) AS CourierName
                """,
                new { title, origin = req.OriginCode, destination = req.DestinationCode, planned = req.PlannedDate, notes = NullIfEmpty(req.Notes), mode = ModeOrDefault(req.TransportMode), courier });

            await SaveStops(conn, row.Id, req.Stops);
            return Results.Created($"/api/admin/trips/{row.Id}", row);
        });

        g.MapPut("/{id:long}", async (long id, UpsertTripRequest req, Database db, CancellationToken ct) =>
        {
            if (req.Status is { } status && !new[] { "planned", "completed", "cancelled" }.Contains(status))
                return Bad("Trip status must be 'planned', 'completed' or 'cancelled'.");

            await using var conn = await db.OpenAsync(ct);
            // 0 = explicit "no courier" (client ids start at 1; anything else
            // must name an existing client row).
            if (req.CourierClientId is { } courier and not 0 && !await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM clients WHERE id = @courier)", new { courier }))
                return Bad("Courier client does not exist.");
            var existing = await conn.QuerySingleOrDefaultAsync<(string? OriginCode, string? DestinationCode)>(
                "SELECT origin_code, destination_code FROM trips WHERE id = @id", new { id });
            if (existing.OriginCode is null)
                return Results.NotFound(new { error = "Trip not found." });
            var effectiveOrigin = req.OriginCode ?? existing.OriginCode;
            var effectiveDestination = req.DestinationCode ?? existing.DestinationCode;
            if (await ValidateStops(conn, effectiveOrigin, effectiveDestination, req.Stops) is { } stopErr)
                return Bad(stopErr);
            if (!ModeValid(req.TransportMode))
                return Bad("Transport mode must be 'train', 'car' or 'unknown'.");

            var row = await conn.QuerySingleOrDefaultAsync<Trip>(
                """
                UPDATE trips
                SET title            = COALESCE(@title, title),
                    origin_code      = COALESCE(@origin, origin_code),
                    destination_code = COALESCE(@destination, destination_code),
                    planned_date     = COALESCE(@planned, planned_date),
                    status           = COALESCE(@status, status),
                    notes            = COALESCE(@notes, notes),
                    transport_mode   = COALESCE(@mode, transport_mode),
                    courier_client_id = CASE WHEN @courier = 0 THEN NULL
                                             ELSE COALESCE(@courier, courier_client_id) END
                WHERE id = @id
                RETURNING id, title, origin_code AS OriginCode, destination_code AS DestinationCode,
                          planned_date AS PlannedDate, status, notes, created_at AS CreatedAt,
                          transport_mode AS TransportMode, courier_client_id AS CourierClientId,
                          (SELECT name FROM clients WHERE id = trips.courier_client_id) AS CourierName
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
                    mode = req.TransportMode,
                    courier = req.CourierClientId,
                });

            if (row is null)
                return Results.NotFound(new { error = "Trip not found." });

            // Cancelled trips release their shipments for re-assignment.
            // Completed ones keep them — they stay attached for the arrival
            // history until each parcel is individually delivered.
            if (row.Status == "cancelled")
                await conn.ExecuteAsync(
                    "UPDATE shipments SET trip_id = NULL, updated_at = now() WHERE trip_id = @id", new { id });

            // Route change: replace the stop list if one was sent.
            if (row.Status != "cancelled")
                await SaveStops(conn, row.Id, req.Stops);

            return Results.Ok(row);
        });

        // Packlist bulk action: "leaving" marks everything picked_up → in_transit,
        // "arrived" marks everything in_transit → at_destination. The logic is
        // shared with the Telegram bot: TripBulk also handles timeline events,
        // trip completion and recipient Telegram digests.
        g.MapPost("/{id:long}/shipments/status", async (long id, TripBulkStatusRequest req, Database db, TripBulk bulk, CancellationToken ct) =>
        {
            var to = req.Status?.Trim() ?? "";
            if (to is not (ShipmentStatus.InTransit or ShipmentStatus.AtDestination))
                return Bad("Bulk status supports only 'in_transit' and 'at_destination'.");

            await using var conn = await db.OpenAsync(ct);
            var result = await bulk.MarkAllAsync(conn, id, to);
            if (result is null)
                return Results.NotFound(new { error = "Trip not found." });

            return Results.Ok(new { from = result.From, to = result.To, updated = result.Updated, skipped = result.Skipped });
        });

        // Spawn one (or the mirrored return) trip from a prior-saved route template.
        g.MapPost("/from-template/{templateId:long}", async (long templateId, InstantiateTripRequest req, Database db, AdminIdentity identity, HttpContext http, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var t = await conn.QuerySingleOrDefaultAsync<TripTemplate>(
                """
                SELECT id, title, origin_code AS OriginCode, destination_code AS DestinationCode,
                       stops AS Stops, transport_mode AS TransportMode, created_at AS CreatedAt
                FROM trip_templates
                WHERE id = @id
                """, new { id = templateId });
            if (t is null)
                return Results.NotFound(new { error = "Template not found." });
            if (t.OriginCode is null || t.DestinationCode is null)
                return Bad("Template needs both an origin and a destination to spawn trips.");

            // Templates predate per-courier assignment; whoever spawns the trips travels them.
            var courier = (await identity.ResolveAsync(conn, http.User))?.Id;
            var created = new List<Trip> { await SpawnTrip(conn, t.Title, t.OriginCode, t.DestinationCode, t.Stops, t.TransportMode, req.PlannedDate, courier) };
            if (req.IncludeReturn)
                created.Add(await SpawnTrip(conn, $"{t.Title} (return)", t.DestinationCode, t.OriginCode,
                    t.Stops.Reverse().ToArray(), t.TransportMode, req.PlannedDate, courier));

            return Results.Created("/api/admin/trips", created);
        });
    }

    private static async Task<Trip> SpawnTrip(
        Npgsql.NpgsqlConnection conn, string title, string origin, string destination,
        string[] stops, string mode, DateTime? planned, long? courier)
    {
        var row = await conn.QuerySingleAsync<Trip>(
            """
            INSERT INTO trips (title, origin_code, destination_code, planned_date, transport_mode, courier_client_id)
            VALUES (@title, @origin, @destination, @planned, @mode, @courier)
            RETURNING id, title, origin_code AS OriginCode, destination_code AS DestinationCode,
                      planned_date AS PlannedDate, status, notes, created_at AS CreatedAt,
                      transport_mode AS TransportMode, courier_client_id AS CourierClientId,
                      (SELECT name FROM clients WHERE id = trips.courier_client_id) AS CourierName
            """,
            new { title, origin, destination, planned, mode, courier });
        await SaveStops(conn, row.Id, stops.ToList());
        return row;
    }

    private static void MapTripTemplates(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/trip-templates").AddEndpointFilter<AdminFilter>();

        g.MapGet("/", async (Database db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var rows = await conn.QueryAsync<TripTemplate>(
                """
                SELECT id, title, origin_code AS OriginCode, destination_code AS DestinationCode,
                       stops AS Stops, transport_mode AS TransportMode, created_at AS CreatedAt
                FROM trip_templates
                ORDER BY title
                """);
            return Results.Ok(rows);
        });

        g.MapPost("/", async (UpsertTripTemplateRequest req, Database db, CancellationToken ct) =>
        {
            var title = req.Title?.Trim();
            if (string.IsNullOrEmpty(title))
                return Bad("Template title is required.");
            if (!ModeValid(req.TransportMode))
                return Bad("Transport mode must be 'train', 'car' or 'unknown'.");

            await using var conn = await db.OpenAsync(ct);
            if (await ValidateStops(conn, req.OriginCode, req.DestinationCode, req.Stops) is { } stopErr)
                return Bad(stopErr);

            var codes = req.Stops ?? [];
            var row = await conn.QuerySingleAsync<TripTemplate>(
                """
                INSERT INTO trip_templates (title, origin_code, destination_code, stops, transport_mode)
                VALUES (@title, @origin, @destination, @stops, @mode)
                RETURNING id, title, origin_code AS OriginCode, destination_code AS DestinationCode,
                          stops AS Stops, transport_mode AS TransportMode, created_at AS CreatedAt
                """,
                new { title, origin = req.OriginCode, destination = req.DestinationCode, stops = codes, mode = ModeOrDefault(req.TransportMode) });
            return Results.Created($"/api/admin/trip-templates/{row.Id}", row);
        });

        g.MapPut("/{id:long}", async (long id, UpsertTripTemplateRequest req, Database db, CancellationToken ct) =>
        {
            if (!ModeValid(req.TransportMode))
                return Bad("Transport mode must be 'train', 'car' or 'unknown'.");

            await using var conn = await db.OpenAsync(ct);
            var existing = await conn.QuerySingleOrDefaultAsync<(string? OriginCode, string? DestinationCode)>(
                "SELECT origin_code, destination_code FROM trip_templates WHERE id = @id", new { id });
            if (existing.OriginCode is null)
                return Results.NotFound(new { error = "Template not found." });
            var effOrigin = req.OriginCode ?? existing.OriginCode;
            var effDest = req.DestinationCode ?? existing.DestinationCode;
            if (await ValidateStops(conn, effOrigin, effDest, req.Stops) is { } stopErr)
                return Bad(stopErr);

            var row = await conn.QuerySingleOrDefaultAsync<TripTemplate>(
                """
                UPDATE trip_templates
                SET title            = COALESCE(@title, title),
                    origin_code      = COALESCE(@origin, origin_code),
                    destination_code = COALESCE(@destination, destination_code),
                    transport_mode   = COALESCE(@mode, transport_mode),
                    stops            = COALESCE(@stops, stops)
                WHERE id = @id
                RETURNING id, title, origin_code AS OriginCode, destination_code AS DestinationCode,
                          stops AS Stops, transport_mode AS TransportMode, created_at AS CreatedAt
                """,
                new
                {
                    id,
                    title = NullIfEmpty(req.Title),
                    origin = req.OriginCode,
                    destination = req.DestinationCode,
                    mode = req.TransportMode,
                    stops = req.Stops is null ? (string[]?)null : req.Stops.ToArray(),
                });
            if (row is null)
                return Results.NotFound(new { error = "Template not found." });
            return Results.Ok(row);
        });

        g.MapDelete("/{id:long}", async (long id, Database db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var deleted = await conn.ExecuteAsync("DELETE FROM trip_templates WHERE id = @id", new { id });
            return deleted == 0 ? Results.NotFound(new { error = "Template not found." }) : Results.Ok(new { ok = true });
        });
    }

    private static bool ModeValid(string? mode) => mode is null || mode is "train" or "car" or "unknown";

    private static string ModeOrDefault(string? mode) => mode ?? "unknown";

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// Validates the intermediate stops of a trip: known city codes, not the
    /// origin or destination themselves, no duplicates. Returns an error
    /// message, or null when the list is usable. (Validation against the DB.)
    /// </summary>
    private static async Task<string?> ValidateStops(
        Npgsql.NpgsqlConnection conn, string? origin, string? destination, List<string>? stops)
    {
        if (stops is null || stops.Count == 0)
            return null;

        var codes = stops
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .Distinct()
            .ToList();

        if (codes.Count > 10)
            return "A trip can have at most 10 stops.";

        var known = (await conn.QueryAsync<string>("SELECT code FROM cities")).ToHashSet();
        foreach (var c in codes.Where(c => !known.Contains(c)))
            return $"Unknown stop city '{c}'.";

        if (origin is not null && codes.Contains(origin))
            return $"Stop '{origin}' is the trip's origin.";
        if (destination is not null && codes.Contains(destination))
            return $"Stop '{destination}' is the trip's destination.";

        return null;
    }

    /// <summary>Replaces the stop list of a trip (positions = list order).
    /// A null stops list means "leave stops untouched".</summary>
    private static async Task SaveStops(Npgsql.NpgsqlConnection conn, long tripId, List<string>? stops)
    {
        if (stops is null)
            return;

        var codes = stops
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .Distinct()
            .ToList();

        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync(
            "DELETE FROM trip_stops WHERE trip_id = @tripId", new { tripId }, tx);
        for (var i = 0; i < codes.Count; i++)
        {
            await conn.ExecuteAsync(
                "INSERT INTO trip_stops (trip_id, city_code, position) VALUES (@tripId, @code, @pos)",
                new { tripId, code = codes[i], pos = i }, tx);
        }
        await tx.CommitAsync();
    }

    private static IResult Bad(string message) => Results.Json(new { error = message },
        statusCode: StatusCodes.Status400BadRequest);

    public sealed record UpsertClientRequest(string Name, string? Contact, string? TelegramChatId, string? Notes);

    public sealed record CreateCityRequest(string Code, string Name);

    public sealed record UpdateCityRequest(string? Name, bool? Active);

    /// <summary>Null = leave the field alone on PUT; 0 = explicitly remove the
    /// courier assignment (client ids start at 1).</summary>
    public sealed record UpsertTripRequest(
        string? Title, string? OriginCode, string? DestinationCode, DateTime? PlannedDate,
        string? Status, string? Notes, List<string>? Stops, string? TransportMode,
        long? CourierClientId = null);

    public sealed record InstantiateTripRequest(DateTime? PlannedDate, bool IncludeReturn);

    public sealed record UpsertTripTemplateRequest(
        string? Title, string? OriginCode, string? DestinationCode, List<string>? Stops, string? TransportMode);

    public sealed record TripBulkStatusRequest(string? Status);
}

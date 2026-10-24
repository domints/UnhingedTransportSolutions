using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Extensions.Options;
using Uts.Api.Db;
using Uts.Api.Domain;

namespace Uts.Api.Services;

/// <summary>Background service: long-polling Telegram bot (commands, claim,
/// mute toggles, courier workflow) plus the outbox sender (retries, backoff).
/// Disabled cleanly when no bot token is configured.</summary>
public sealed class TelegramBot(
    IOptions<TelegramOptions> options,
    IHttpClientFactory httpFactory,
    BotState botState,
    Database db,
    TelegramNotifier telegram,
    TripBulk tripBulk,
    ILogger<TelegramBot> logger) : BackgroundService
{
    private const string ApiBase = "https://api.telegram.org/";
    private static readonly JsonSerializerOptions Json = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.BotToken))
        {
            logger.LogInformation("Telegram: no bot token configured — module disabled.");
            return;
        }

        var http = httpFactory.CreateClient();
        // Token goes into the base address, NOT the request path: the token
        // contains a colon, so a string like "bot8966223705:AA…/getMe" is parsed
        // as an absolute URI with scheme "bot8966223705" and HttpClient throws
        // «The 'bot…' scheme is not supported». Relative paths ("getMe") resolve
        // against the base — no colon parsing involved.
        http.BaseAddress = new Uri($"{ApiBase}bot{opts.BotToken}/");
        http.Timeout = TimeSpan.FromSeconds(70); // long polling needs patience

        string? username;
        try
        {
            username = TryGetUsername(await CallAsync(http, "getMe", null, stoppingToken));
        }
        catch (Exception ex)
        {
            // A flaky network / Telegram hiccup at boot must not take the whole
            // app down — the bot is an optional module.
            logger.LogWarning(ex, "Telegram: getMe failed — module disabled for this run.");
            return;
        }
        if (string.IsNullOrEmpty(username))
        {
            logger.LogError("Telegram: getMe failed — module disabled. Check the bot token.");
            return;
        }
        botState.BotUsername = username;
        logger.LogInformation("Telegram bot active as @{name}.", username);

        long offset = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainOutboxAsync(http, opts, stoppingToken);

                var updates = await GetUpdatesAsync(http, opts.BotToken, offset, stoppingToken);
                foreach (var u in updates)
                {
                    offset = u.UpdateId + 1;
                    try
                    {
                        await HandleUpdateAsync(http, opts, u, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Telegram: failed to handle update {id}.", u.UpdateId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Telegram: polling failure — retrying in 5 s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    // ---------------------------------------------------------------- updates

    private async Task HandleUpdateAsync(HttpClient http, TelegramOptions opts, TgUpdate u, CancellationToken ct)
    {
        if (u.CallbackQuery is { } cb)
        {
            var chatId = (cb.Message?.Chat.Id ?? cb.From.Id).ToString();
            await HandleCallbackAsync(http, opts, chatId, cb, ct);
            return;
        }

        var msg = u.Message;
        if (msg?.Text is not { } text)
            return;
        if (!text.StartsWith('/'))
        {
            await SendAsync(http, opts.BotToken!, msg.Chat.Id.ToString(), TelegramTexts.Help, null, ct);
            return;
        }

        var chatId2 = msg.Chat.Id.ToString();
        var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries);
        var command = parts[0].Split('@')[0].ToLowerInvariant();

        switch (command)
        {
            case "/start":
                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])) await ClaimAsync(http, chatId2, parts[1], ct);
                else await SendAsync(http, opts.BotToken!, chatId2, TelegramTexts.Help, null, ct);
                break;

            case "/help":
                await SendAsync(http, opts.BotToken!, chatId2, TelegramTexts.Help, null, ct);
                break;

            case "/notifications":
                await ShowNotificationTogglesAsync(http, chatId2, ct);
                break;

            case "/leaving":
                await PickTripAsync(http, chatId2, ShipmentStatus.InTransit,
                    "Najbliższa podróż — oznaczyć paczki jako w drodze? 🚆", leavePrefix, ct);
                break;

            case "/arrived":
                await PickTripAsync(http, chatId2, ShipmentStatus.AtDestination,
                    "Dotarłeś? Oznaczyć paczki jako dotarłe do miasta docelowego? 📍", arrivePrefix, ct);
                break;

            case "/deliver":
                await ShowDeliverableAsync(http, chatId2, ct);
                break;

            case "/packlist":
                await ShowPacklistAsync(http, chatId2, ct);
                break;

            default:
                await SendAsync(http, opts.BotToken!, chatId2, TelegramTexts.Help, null, ct);
                break;
        }
    }

    private const string leavePrefix = "leave:";
    private const string arrivePrefix = "arrive:";

    // ---------------------------------------------------------------- callbacks

    private async Task HandleCallbackAsync(HttpClient http, TelegramOptions opts, string chatId, TgCallbackQuery cb, CancellationToken ct)
    {
        var data = cb.Data ?? "";
        async Task Finish(string? toast = null) => await AnswerCallbackAsync(http, opts.BotToken!, cb.Id, toast, ct);

        if (data.StartsWith("mute:") || data.StartsWith("unmute:"))
        {
            await SetMuteAsync(http, chatId, data, ct);
            await Finish();
            return;
        }

        if (data.StartsWith(leavePrefix) || data.StartsWith(arrivePrefix))
        {
            if (!IsCourier(opts, chatId))
            {
                await Finish(TelegramTexts.CourierOnly);
                return;
            }
            var to = data.StartsWith(leavePrefix) ? ShipmentStatus.InTransit : ShipmentStatus.AtDestination;
            if (!long.TryParse(data[(data.IndexOf(':') + 1)..], out var tripId))
            {
                await Finish();
                return;
            }

            await using var conn = await db.OpenAsync(ct);
            var result = await tripBulk.MarkAllAsync(conn, tripId, to);
            if (result is null)
            {
                await SendAsync(http, opts.BotToken!, chatId, "Nie ma takiej podróży.", null, ct);
            }
            else
            {
                var summary = $"Oznaczono {result.Updated.Count} paczek: {TelegramTexts.StatusLabel(result.To)} ✅";
                if (result.Skipped.Count > 0)
                    summary += "\nPominięto (status niedopuszczający zmiany): " +
                               string.Join(", ", result.Skipped.Select(s => s.ToString()).Take(10));
                await SendAsync(http, opts.BotToken!, chatId, summary, null, ct);
            }
            await Finish();
            return;
        }

        if (data.StartsWith("deliver:"))
        {
            if (!IsCourier(opts, chatId))
            {
                await Finish(TelegramTexts.CourierOnly);
                return;
            }
            if (!long.TryParse(data["deliver:".Length..], out var shipId))
            {
                await Finish();
                return;
            }
            await using var conn = await db.OpenAsync(ct);
            var sh = await conn.QuerySingleOrDefaultAsync<(long Id, string Number)>(
                "SELECT id, tracking_number FROM shipments WHERE id = @id", new { id = shipId });
            if (sh.Id == 0)
            {
                await Finish();
                return;
            }
            await SendAsync(http, opts.BotToken!, chatId, $"Dostarczyć {sh.Number}?",
                Inline(("✅ Tak, dostarczona", $"deliverok:{shipId}"), ("❌ Nie", "noop")), ct);
            await Finish();
            return;
        }

        if (data.StartsWith("deliverok:"))
        {
            if (!IsCourier(opts, chatId))
            {
                await Finish(TelegramTexts.CourierOnly);
                return;
            }
            if (!long.TryParse(data["deliverok:".Length..], out var shipId2))
            {
                await Finish();
                return;
            }

            await using var conn = await db.OpenAsync(ct);
            var sh2 = await conn.QuerySingleOrDefaultAsync<(long Id, string Number)>(
                "SELECT id, tracking_number FROM shipments WHERE id = @id AND status IN ('in_transit','at_destination')",
                new { id = shipId2 });
            if (sh2.Id == 0)
            {
                await Finish("Ta paczka nie jest już w drodze.");
                return;
            }

            await using var tx = await conn.BeginTransactionAsync(ct);
            await conn.ExecuteAsync(
                "UPDATE shipments SET status = 'delivered', updated_at = now(), delivered_at = now() WHERE id = @id",
                new { id = shipId2 }, tx);
            await conn.ExecuteAsync(
                "INSERT INTO shipment_events (shipment_id, type, message) VALUES (@id, 'status', @msg)",
                new { id = shipId2, msg = "Status updated: DELIVERED" }, tx);
            await telegram.EnqueueShipmentStatusAsync(conn, tx, shipId2, "delivered");
            await tx.CommitAsync(ct);

            await Finish();
            await SendAsync(http, opts.BotToken!, chatId, $"Paczka {sh2.Number} dostarczona ✅", null, ct);
            return;
        }

        await Finish();
    }

    private async Task SetMuteAsync(HttpClient http, string chatId, string data, CancellationToken ct)
    {
        var mute = data.StartsWith("mute:");
        if (!long.TryParse(data[(data.IndexOf(':') + 1)..], out var clientId))
            return;

        await using var conn = await db.OpenAsync(ct);
        var owned = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM clients WHERE id = @id AND telegram_chat_id::text = @chatId)",
            new { id = clientId, chatId });
        if (!owned)
            return; // another chat tried to flip someone else's switch

        await conn.ExecuteAsync("UPDATE clients SET telegram_notifications = @v WHERE id = @id",
            new { id = clientId, v = !mute });

        var name = await conn.ExecuteScalarAsync<string>("SELECT name FROM clients WHERE id = @id", new { id = clientId });
        await dbSend(http, chatId, mute
            ? $"Powiadomienia wyciszone 🔕 ({name})"
            : $"Powiadomienia włączone 🔔 ({name})", null, ct);
    }

    private async Task ClaimAsync(HttpClient http, string chatId, string code, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var client = await conn.QuerySingleOrDefaultAsync<(long Id, string Name)>(
            "SELECT id, name FROM clients WHERE telegram_claim_code = @code AND telegram_claim_expires > now()",
            new { code = code.Trim().ToUpperInvariant() });
        if (client.Id == 0)
        {
            await dbSend(http, chatId, TelegramTexts.ClaimInvalid, null, ct);
            return;
        }

        await conn.ExecuteAsync(
            """
            UPDATE clients
            SET telegram_chat_id = @chatId,
                telegram_claim_code = NULL,
                telegram_claim_expires = NULL
            WHERE id = @id
            """, new { chatId, id = client.Id });

        await dbSend(http, chatId, $"{TelegramTexts.ClaimOk}\nPowiązane z: {client.Name}.", null, ct);
    }

    private async Task ShowNotificationTogglesAsync(HttpClient http, string chatId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = (await conn.QueryAsync<(long Id, string Name, bool Enabled)>(
            "SELECT id, name, telegram_notifications FROM clients WHERE telegram_chat_id::text = @chatId ORDER BY name",
            new { chatId })).AsList();
        if (rows.Count == 0)
        {
            await dbSend(http, chatId, TelegramTexts.NoBinding, null, ct);
            return;
        }

        foreach (var c in rows)
        {
            var state = c.Enabled ? "włączone 🔔" : "wyłączone 🔕";
            var button = c.Enabled
                ? ("🔕 Wycisz powiadomienia", $"mute:{c.Id}")
                : ("🔔 Włącz powiadomienia", $"unmute:{c.Id}");
            await dbSend(http, chatId, $"Powiadomienia dla {c.Name}: {state}", Inline(button), ct);
        }
    }

    // ---------------------------------------------------------------- courier

    private bool IsCourier(TelegramOptions opts, string chatId) => opts.CourierChatIdSet().Contains(chatId);

    private async Task PickTripAsync(HttpClient http, string chatId, string to, string question, string callbackPrefix, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var trips = (await conn.QueryAsync<(long Id, string Title, DateTime? PlannedDate, int Count)>(
            """
            SELECT t.id, t.title, t.planned_date, COUNT(s.id)::int
            FROM trips t
            LEFT JOIN shipments s ON s.trip_id = t.id
            WHERE t.status = 'planned'
            GROUP BY t.id, t.title, t.planned_date
            ORDER BY (t.planned_date < CURRENT_DATE) ASC, t.planned_date ASC NULLS LAST, t.created_at DESC
            LIMIT 8
            """)).AsList();
        if (trips.Count == 0)
        {
            await dbSend(http, chatId, "Brak zaplanowanych podróży.", null, ct);
            return;
        }

        var buttons = trips
            .Select(t => ($"{t.Title} ({(t.PlannedDate is { } d ? d.ToString("dd.MM") : "—")}) · paczek: {t.Count}", $"{callbackPrefix}{t.Id}"))
            .ToArray();
        await dbSend(http, chatId, question, Inline(buttons), ct);
    }

    private async Task ShowDeliverableAsync(HttpClient http, string chatId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = (await conn.QueryAsync<(long Id, string Number, string RecipientName)>(
            """
            SELECT s.id, s.tracking_number, r.name
            FROM shipments s
            JOIN clients r ON r.id = s.recipient_id
            WHERE s.status IN ('in_transit','at_destination')
            ORDER BY s.tracking_number
            LIMIT 20
            """)).AsList();
        if (rows.Count == 0)
        {
            await dbSend(http, chatId, "Brak paczek w drodze — wszystko dostarczone. 🎉", null, ct);
            return;
        }

        var buttons = rows
            .Select(s => ($"{s.Number} — {s.RecipientName}", $"deliver:{s.Id}"))
            .ToArray();
        await dbSend(http, chatId, "Które paczki dostarczyłeś?", Inline(buttons), ct);
    }

    private async Task ShowPacklistAsync(HttpClient http, string chatId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var trip = await conn.QuerySingleOrDefaultAsync<(long Id, string Title)>(
            """
            SELECT t.id, t.title
            FROM trips t
            WHERE t.status = 'planned'
            ORDER BY (t.planned_date < CURRENT_DATE) ASC, t.planned_date ASC NULLS LAST, t.created_at DESC
            LIMIT 1
            """);
        if (trip.Id == 0)
        {
            await dbSend(http, chatId, "Brak zaplanowanych podróży.", null, ct);
            return;
        }

        var haul = (await conn.QueryAsync<(string Number, string Status, string Recipient, string Description)>(
            """
            SELECT s.tracking_number, s.status, r.name, s.description
            FROM shipments s
            JOIN clients r ON r.id = s.recipient_id
            WHERE s.trip_id = @id
            ORDER BY s.id
            """, new { id = trip.Id })).AsList();
        if (haul.Count == 0)
        {
            await dbSend(http, chatId, $"Packlist „{trip.Title}”: pusto. 📭", null, ct);
            return;
        }

        var lines = haul.Select(h =>
            $"• {h.Number} — {TelegramTexts.StatusLabel(h.Status)} — {h.Recipient} — {Truncate(h.Description, 40)}");
        await dbSend(http, chatId, $"Packlist „{trip.Title}”:\n{string.Join('\n', lines)}", null, ct);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ---------------------------------------------------------------- http plumbing

    private async Task DrainOutboxAsync(HttpClient http, TelegramOptions opts, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var pending = (await conn.QueryAsync<(long Id, string ChatId, string Text, string? KeyboardJson)>(
            """
            SELECT id, chat_id, text, keyboard_json
            FROM telegram_outbox
            WHERE sent_at IS NULL
              AND attempts < 8
              AND (last_attempt IS NULL OR last_attempt < now() - make_interval(secs => 60 * power(2, attempts)))
            ORDER BY id
            LIMIT 20
            """)).AsList();

        foreach (var m in pending)
        {
            var ok = await SendAsync(http, opts.BotToken!, m.ChatId, m.Text, m.KeyboardJson, ct);
            if (ok)
                await conn.ExecuteAsync(
                    "UPDATE telegram_outbox SET sent_at = now(), last_attempt = now() WHERE id = @id", new { m.Id });
            else
                await conn.ExecuteAsync(
                    "UPDATE telegram_outbox SET last_attempt = now(), attempts = attempts + 1, last_error = @err WHERE id = @id",
                    new { m.Id, err = "send failed" });
        }
    }

    private async Task<List<TgUpdate>> GetUpdatesAsync(HttpClient http, string token, long offset, CancellationToken ct)
    {
        var resp = await http.GetAsync($"getUpdates?offset={offset}&timeout=25", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("result", out var result)
            ? result.Deserialize<List<TgUpdate>>(Json) ?? []
            : [];
    }

    private async Task<JsonDocument?> CallAsync(HttpClient http, string method, object? body, CancellationToken ct)
    {
        using var content = body is null
            ? null
            : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = body is null
            ? await http.GetAsync(method, ct)
            : await http.PostAsync(method, content!, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonDocument.Parse(raw);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> SendAsync(HttpClient http, string token, string chatId, string text, string? keyboardJson, CancellationToken ct)
    {
        var payload = new JsonObject { ["chat_id"] = long.Parse(chatId), ["text"] = text };
        if (!string.IsNullOrEmpty(keyboardJson))
            payload["reply_markup"] = JsonNode.Parse(keyboardJson);
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var resp = await http.PostAsync("sendMessage", content, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task dbSend(HttpClient http, string chatId, string text, string? keyboardJson, CancellationToken ct)
        => await SendAsync(http, options.Value.BotToken!, chatId, text, keyboardJson, ct);

    private static async Task AnswerCallbackAsync(HttpClient http, string token, string callbackId, string? toast, CancellationToken ct)
    {
        var payload = new JsonObject { ["callback_query_id"] = callbackId };
        if (toast is not null)
            payload["text"] = toast;
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var _ = await http.PostAsync("answerCallbackQuery", content, ct);
        }
        catch
        {
            /* non-critical */
        }
    }

    private static string Inline(params (string Text, string Data)[] buttons)
    {
        var board = new JsonObject
        {
            ["inline_keyboard"] = new JsonArray(buttons
                .Select(b => (JsonNode)new JsonObject { ["text"] = b.Text, ["callback_data"] = b.Data })
                .ToArray()),
        };
        return board.ToJsonString();
    }

    // ---------------------------------------------------------------- telegram DTOs

    private sealed class TgUpdate
    {
        [JsonPropertyName("update_id")] public long UpdateId { get; set; }
        [JsonPropertyName("message")] public TgMessage? Message { get; set; }
        [JsonPropertyName("callback_query")] public TgCallbackQuery? CallbackQuery { get; set; }
    }

    private sealed class TgMessage
    {
        [JsonPropertyName("message_id")] public long MessageId { get; set; }
        [JsonPropertyName("chat")] public TgChat Chat { get; set; } = new();
        [JsonPropertyName("text")] public string? Text { get; set; }
    }

    private sealed class TgChat
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("first_name")] public string? FirstName { get; set; }
    }

    private sealed class TgCallbackQuery
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("from")] public TgFrom From { get; set; } = new();
        [JsonPropertyName("message")] public TgMessage? Message { get; set; }
        [JsonPropertyName("data")] public string? Data { get; set; }
    }

    private sealed class TgFrom
    {
        [JsonPropertyName("id")] public long Id { get; set; }
    }

    private static string? TryGetUsername(JsonDocument? me)
    {
        return me is not null
               && me.RootElement.TryGetProperty("result", out var r)
               && r.TryGetProperty("username", out var u)
            ? u.GetString()
            : null;
    }
}

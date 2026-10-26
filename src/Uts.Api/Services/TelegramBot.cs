using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Uts.Api.Db;
using Uts.Api.Domain;

namespace Uts.Api.Services;

/// <summary>Background service: long-polling Telegram bot (commands, claim,
/// mute toggles, courier workflow) plus the outbox sender (retries, backoff).
/// Built on the Telegram.Bot library; disabled cleanly when no bot token is
/// configured.</summary>
public sealed class TelegramBot(
    IOptions<TelegramOptions> options,
    BotState botState,
    Database db,
    TelegramNotifier telegram,
    TripBulk tripBulk,
    ILogger<TelegramBot> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.BotToken))
        {
            logger.LogInformation("Telegram: no bot token configured — module disabled.");
            return;
        }

        var bot = new TelegramBotClient(opts.BotToken);

        User me;
        try
        {
            me = await bot.GetMe(stoppingToken);
        }
        catch (Exception ex)
        {
            // A flaky network / Telegram hiccup at boot must not take the whole
            // app down — the bot is an optional module (and the host is not
            // configured to stop on background-service exceptions either).
            logger.LogWarning(ex, "Telegram: getMe failed — module disabled for this run.");
            return;
        }
        if (string.IsNullOrEmpty(me.Username))
        {
            logger.LogError("Telegram: getMe returned no username — module disabled. Check the bot token.");
            return;
        }
        botState.BotUsername = me.Username;
        logger.LogInformation("Telegram bot active as @{name}.", me.Username);

        var offset = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainOutboxAsync(bot, opts, stoppingToken);

                var updates = await bot.GetUpdates(offset, timeout: 25, cancellationToken: stoppingToken);
                foreach (var u in updates)
                {
                    offset = (int)u.Id + 1;
                    try
                    {
                        await HandleUpdateAsync(bot, opts, u, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Telegram: failed to handle update {id}.", u.Id);
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

    private async Task HandleUpdateAsync(TelegramBotClient bot, TelegramOptions opts, Update u, CancellationToken ct)
    {
        if (u.CallbackQuery is { } cb)
        {
            var chatId = cb.Message?.Chat.Id ?? cb.From.Id;
            await HandleCallbackAsync(bot, opts, chatId, cb, ct);
            return;
        }

        var msg = u.Message;
        if (msg?.Text is not { } text)
            return;
        var chatId2 = msg.Chat.Id;
        if (!text.StartsWith('/'))
        {
            await SendAsync(bot, chatId2, TelegramTexts.Help, null, ct);
            return;
        }

        var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries);
        var command = parts[0].Split('@')[0].ToLowerInvariant();

        switch (command)
        {
            case "/start":
                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])) await ClaimAsync(bot, chatId2, parts[1], ct);
                else await SendAsync(bot, chatId2, TelegramTexts.Help, null, ct);
                break;

            case "/help":
                await SendAsync(bot, chatId2, TelegramTexts.Help, null, ct);
                break;

            case "/notifications":
                await ShowNotificationTogglesAsync(bot, chatId2, ct);
                break;

            case "/leaving":
                await PickTripAsync(bot, chatId2, ShipmentStatus.InTransit,
                    "Najbliższa podróż — oznaczyć paczki jako w drodze? 🚆", leavePrefix, ct);
                break;

            case "/arrived":
                await PickTripAsync(bot, chatId2, ShipmentStatus.AtDestination,
                    "Dotarłeś? Oznaczyć paczki jako dotarłe do miasta docelowego? 📍", arrivePrefix, ct);
                break;

            case "/deliver":
                await ShowDeliverableAsync(bot, chatId2, ct);
                break;

            case "/packlist":
                await ShowPacklistAsync(bot, chatId2, ct);
                break;

            default:
                await SendAsync(bot, chatId2, TelegramTexts.Help, null, ct);
                break;
        }
    }

    private const string leavePrefix = "leave:";
    private const string arrivePrefix = "arrive:";

    // ---------------------------------------------------------------- callbacks

    private async Task HandleCallbackAsync(TelegramBotClient bot, TelegramOptions opts, long chatId, CallbackQuery cb, CancellationToken ct)
    {
        var data = cb.Data ?? "";
        async Task Finish(string? toast = null)
        {
            try
            {
                await bot.AnswerCallbackQuery(cb.Id, toast, cancellationToken: ct);
            }
            catch { /* cosmetic, never fatal */ }
        }

        if (data.StartsWith("mute:") || data.StartsWith("unmute:"))
        {
            await SetMuteAsync(bot, chatId, data, ct);
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
                await SendAsync(bot, chatId, "Nie ma takiej podróży.", null, ct);
            }
            else
            {
                var summary = $"Oznaczono {result.Updated.Count} paczek: {TelegramTexts.StatusLabel(result.To)}";
                summary += ShipmentStatus.AtDestination == to ? " 📍" : " 🚆";
                if (result.Skipped.Count > 0)
                    summary += "\nPominięto (status niedopuszczający zmiany): " +
                               string.Join(", ", result.Skipped.Select(s => s.ToString()).Take(10));
                await SendAsync(bot, chatId, summary, null, ct);
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
            await SendAsync(bot, chatId, $"Dostarczyć {sh.Number}?",
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
            await SendAsync(bot, chatId, $"Paczka {sh2.Number} dostarczona ✅", null, ct);
            return;
        }

        await Finish();
    }

    private async Task SetMuteAsync(TelegramBotClient bot, long chatId, string data, CancellationToken ct)
    {
        var mute = data.StartsWith("mute:");
        if (!long.TryParse(data[(data.IndexOf(':') + 1)..], out var clientId))
            return;

        await using var conn = await db.OpenAsync(ct);
        var owned = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM clients WHERE id = @id AND telegram_chat_id = @chatId)",
            new { id = clientId, chatId = chatId.ToString(CultureInfo.InvariantCulture) });
        if (!owned)
            return; // another chat tried to flip someone else's switch

        await conn.ExecuteAsync("UPDATE clients SET telegram_notifications = @v WHERE id = @id",
            new { id = clientId, v = !mute });

        var name = await conn.ExecuteScalarAsync<string>("SELECT name FROM clients WHERE id = @id", new { id = clientId });
        await SendAsync(bot, chatId, mute
            ? $"Powiadomienia wyciszone 🔕 ({name})"
            : $"Powiadomienia włączone 🔔 ({name})", null, ct);
    }

    private async Task ClaimAsync(TelegramBotClient bot, long chatId, string code, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var client = await conn.QuerySingleOrDefaultAsync<(long Id, string Name)>(
            "SELECT id, name FROM clients WHERE telegram_claim_code = @code AND telegram_claim_expires > now()",
            new { code = code.Trim().ToUpperInvariant() });
        if (client.Id == 0)
        {
            await SendAsync(bot, chatId, TelegramTexts.ClaimInvalid, null, ct);
            return;
        }

        await conn.ExecuteAsync(
            """
            UPDATE clients
            SET telegram_chat_id = @chatId,
                telegram_claim_code = NULL,
                telegram_claim_expires = NULL,
                -- linking = (re-)consent: a re-claimed chat that was muted
                -- before (or under an old chat id) starts receiving again
                telegram_notifications = TRUE
            WHERE id = @id
            """, new { chatId = chatId.ToString(CultureInfo.InvariantCulture), id = client.Id });

        await SendAsync(bot, chatId, $"{TelegramTexts.ClaimOk}\nPowiązane z: {client.Name}.", null, ct);
    }

    private async Task ShowNotificationTogglesAsync(TelegramBotClient bot, long chatId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = (await conn.QueryAsync<(long Id, string Name, bool Enabled)>(
            "SELECT id, name, telegram_notifications FROM clients WHERE telegram_chat_id = @chatId ORDER BY name",
            new { chatId = chatId.ToString(CultureInfo.InvariantCulture) })).AsList();
        if (rows.Count == 0)
        {
            await SendAsync(bot, chatId, TelegramTexts.NoBinding, null, ct);
            return;
        }

        foreach (var c in rows)
        {
            var state = c.Enabled ? "włączone 🔔" : "wyłączone 🔕";
            var button = c.Enabled
                ? ("🔕 Wycisz powiadomienia", $"mute:{c.Id}")
                : ("🔔 Włącz powiadomienia", $"unmute:{c.Id}");
            await SendAsync(bot, chatId, $"Powiadomienia dla {c.Name}: {state}", Inline(button), ct);
        }
    }

    // ---------------------------------------------------------------- courier

    private bool IsCourier(TelegramOptions opts, long chatId) =>
        opts.CourierChatIdSet().Contains(chatId.ToString(CultureInfo.InvariantCulture));

    private async Task PickTripAsync(TelegramBotClient bot, long chatId, string to, string question, string callbackPrefix, CancellationToken ct)
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
            await SendAsync(bot, chatId, "Brak zaplanowanych podróży.", null, ct);
            return;
        }

        await SendAsync(bot, chatId, question, Keyboard(trips.Select(t =>
            (Text: $"{t.Title} ({(t.PlannedDate is { } d ? d.ToString("dd.MM") : "—")}) · paczek: {t.Count}",
             Cb: $"{callbackPrefix}{t.Id}")).ToArray()), ct);
    }

    private async Task ShowDeliverableAsync(TelegramBotClient bot, long chatId, CancellationToken ct)
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
            await SendAsync(bot, chatId, "Brak paczek w drodze — wszystko dostarczone. 🎉", null, ct);
            return;
        }

        await SendAsync(bot, chatId, "Które paczki dostarczyłeś?",
            Keyboard(rows.Select(s => (Text: $"{s.Number} — {s.RecipientName}", Cb: $"deliver:{s.Id}")).ToArray()), ct);
    }

    private async Task ShowPacklistAsync(TelegramBotClient bot, long chatId, CancellationToken ct)
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
            await SendAsync(bot, chatId, "Brak zaplanowanych podróży.", null, ct);
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
            await SendAsync(bot, chatId, $"Packlist „{trip.Title}”: pusto. 📭", null, ct);
            return;
        }

        var lines = haul.Select(h =>
            $"• {h.Number} — {TelegramTexts.StatusLabel(h.Status)} — {h.Recipient} — {Truncate(h.Description, 40)}");
        await SendAsync(bot, chatId, $"Packlist „{trip.Title}”:\n{string.Join('\n', lines)}", null, ct);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ---------------------------------------------------------------- sending

    private async Task DrainOutboxAsync(TelegramBotClient bot, TelegramOptions opts, CancellationToken ct)
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
            var ok = long.TryParse(m.ChatId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var chatId)
                     && await SendAsync(bot, chatId, m.Text, ParseKeyboard(m.KeyboardJson), ct);
            if (ok)
                await conn.ExecuteAsync(
                    "UPDATE telegram_outbox SET sent_at = now(), last_attempt = now() WHERE id = @id", new { m.Id });
            else
                await conn.ExecuteAsync(
                    "UPDATE telegram_outbox SET last_attempt = now(), attempts = attempts + 1, last_error = @err WHERE id = @id",
                    new { m.Id, err = "send failed" });
        }
    }

    /// <summary>Send + swallow network/API errors (returns false → outbox marks
    /// the attempt; retry cap and backoff live in the DB).</summary>
    private async Task<bool> SendAsync(TelegramBotClient bot, long chatId, string text, InlineKeyboardMarkup? keyboard, CancellationToken ct)
    {
        try
        {
            await bot.SendMessage(chatId, text, replyMarkup: keyboard, cancellationToken: ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Telegram: SendTextMessage failed ({msg}).", ex.Message);
            return false;
        }
    }

    /// <summary>One-row keyboard of callback buttons.</summary>
    private static InlineKeyboardMarkup Keyboard(params (string Text, string Cb)[] buttons)
        => new(buttons.Select(b => new[] { InlineKeyboardButton.WithCallbackData(b.Text, b.Cb) }));

    private static InlineKeyboardMarkup Inline(params (string Text, string Data)[] buttons) => Keyboard(buttons);

    /// <summary>Outbox rows store a serialized Telegram inline keyboard (URL
    /// buttons pointing at the tracking page). Parse it back into the
    /// library's markup type.</summary>
    private static InlineKeyboardMarkup? ParseKeyboard(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("inline_keyboard", out var rows))
                return null;
            var markup = rows.EnumerateArray()
                .Select(row => row.EnumerateArray().Select(button =>
                {
                    var text = button.TryGetProperty("text", out var t) ? t.GetString() ?? "?" : "?";
                    return button.TryGetProperty("url", out var url)
                        ? InlineKeyboardButton.WithUrl(text, url.GetString() ?? "")
                        : InlineKeyboardButton.WithCallbackData(text,
                              button.TryGetProperty("callback_data", out var d) ? d.GetString() ?? "" : "");
                }).ToArray());
            return new InlineKeyboardMarkup(markup);
        }
        catch
        {
            return null;
        }
    }
}

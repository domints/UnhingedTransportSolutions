using Dapper;
using Npgsql;
using Uts.Api.Db;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Uts.Api.Services;

/// <summary>Writes Telegram notifications into the outbox. Status/trip pushes
/// are embedded in the same DB transaction as the change itself (nothing is
/// lost if the process dies right after a commit); admin alerts open their own
/// connection and are fire-and-forget.</summary>
public sealed class TelegramNotifier(TelegramOptions options, Database db, ILogger<TelegramNotifier> logger)
{
    public const int MaxPerMessageButtons = 5;

    /// <summary>Insert a row into the outbox using the caller's connection/transaction.</summary>
    public async Task EnqueueAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string chatId, string text, string? keyboardJson = null)
    {
        if (string.IsNullOrWhiteSpace(chatId))
            return;
        await conn.ExecuteAsync(
            "INSERT INTO telegram_outbox (chat_id, text, keyboard_json) VALUES (@chatId, @text, @keyboard)",
            new { chatId, text, keyboard = keyboardJson }, tx);
        // Access-code lines ("🔑 …") must never reach the log.
        var visible = string.Join(' ', text.Split('\n').Where(l => !l.StartsWith("🔑")));
        logger.LogInformation("Telegram: queued outbox row → chat {chatId}, {len} chars, keyboard={kb}: {preview}",
            chatId, text.Length, keyboardJson != null, Trunc(visible));
    }

    /// <summary>Status change push to the recipient (skips missing chat / muted).</summary>
    public async Task EnqueueShipmentStatusAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long shipmentId, string status)
    {
        var r = await LoadRecipientAsync(conn, shipmentId);
        if (r?.Number is null)
            return;
        if (r.ChatId is null)
        {
            logger.LogInformation("Telegram: recipient push SKIPPED for {number} ({status}) — recipient has no chat id.",
                r.Number, status);
            return;
        }
        if (!r.Notifications)
        {
            logger.LogInformation("Telegram: recipient push SKIPPED for {number} ({status}) — recipient muted notifications.",
                r.Number, status);
            return;
        }

        var text = WithAccessCode(TelegramTexts.ShipmentStatus(r.Number, status, r.DestinationName, r.CourierName), r.Number, r.Pin);
        logger.LogInformation("Telegram: status push for {number} ({status}) → chat {chatId}.",
            r.Number, status, r.ChatId);
        await EnqueueAsync(conn, tx, r.ChatId, text, TrackKeyboard(r.Number, r.Pin));
    }

    /// <summary>Correction push after a delivered parcel was rolled back
    /// (delivered → at_destination): the recipient already got the "delivered"
    /// message, so a silent timeline-only change would look like a glitch.</summary>
    public async Task EnqueueStatusCorrectionAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long shipmentId, string status)
    {
        var r = await LoadRecipientAsync(conn, shipmentId);
        if (r?.Number is null || !r.Notifications || r.ChatId is null)
            return;

        logger.LogInformation("Telegram: status correction for {number} ({status}) → chat {chatId}.",
            r.Number, status, r.ChatId);
        await EnqueueAsync(conn, tx, r.ChatId,
            WithAccessCode(TelegramTexts.StatusCorrection(r.Number, status), r.Number, r.Pin),
            TrackKeyboard(r.Number, r.Pin));
    }

    /// <summary>
    /// Operational push to the courier who just received the parcel. This is
    /// work, not a recipient notification: it goes to the holder's chat even if
    /// they muted recipient updates (a chat can serve several client rows).
    /// The access code is deliberately NOT included here — the courier hands it
    /// to the recipient, they don't need it for their own work.
    /// </summary>
    public async Task EnqueueCourierHandoverAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long shipmentId, long courierClientId)
    {
        var r = await conn.QuerySingleOrDefaultAsync<(string? ChatId, string? Number, string? DestinationName)>(
            """
            SELECT c.telegram_chat_id::text, s.tracking_number, dc.name
            FROM clients c
            JOIN shipments s ON s.id = @id
            LEFT JOIN cities dc ON dc.code = s.destination_code
            WHERE c.id = @courierClientId
            """, new { id = shipmentId, courierClientId });
        if (r.Number is null)
            return;
        if (r.ChatId is null)
        {
            logger.LogInformation("Telegram: handover push SKIPPED for {number} — custodian has no chat id.", r.Number);
            return;
        }

        logger.LogInformation("Telegram: handover push for {number} → custodian chat {chatId}.", r.Number, r.ChatId);
        await EnqueueAsync(conn, tx, r.ChatId, TelegramTexts.HandoverToCourier(r.Number, r.DestinationName),
            TrackKeyboard(r.Number));
    }

    /// <summary>Short recipient push: "your parcel is now with Wiktor".</summary>
    public async Task EnqueueHandoverRecipientAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long shipmentId, string? courierName)
    {
        var r = await LoadRecipientAsync(conn, shipmentId);
        if (r?.Number is null || !r.Notifications || r.ChatId is null)
            return;

        logger.LogInformation("Telegram: handover push for {number} → recipient chat {chatId} (now with {courier}).",
            r.Number, r.ChatId, courierName);
        await EnqueueAsync(conn, tx, r.ChatId,
            WithAccessCode(TelegramTexts.HandoverRecipient(r.Number, courierName), r.Number, r.Pin),
            TrackKeyboard(r.Number, r.Pin));
    }

    private sealed record RecipientTarget(
        string? ChatId, bool Notifications, string? Number, string? DestinationName, string? CourierName, string? Pin);

    private static Task<RecipientTarget?> LoadRecipientAsync(NpgsqlConnection conn, long shipmentId)
        => conn.QuerySingleOrDefaultAsync<RecipientTarget>(
            """
            SELECT c.telegram_chat_id::text AS ChatId,
                   c.telegram_notifications AS Notifications,
                   s.tracking_number        AS Number,
                   dc.name                  AS DestinationName,
                   cc.name                  AS CourierName,
                   s.pin                    AS Pin
            FROM shipments s
            JOIN clients c ON c.id = s.recipient_id
            JOIN cities dc ON dc.code = s.destination_code
            LEFT JOIN trips t ON t.id = s.trip_id
            LEFT JOIN clients cc ON cc.id = t.courier_client_id
            WHERE s.id = @id
            """, new { id = shipmentId });

    /// <summary>Push when the internal notes are updated (or cleared): the
    /// recipient receives the new content directly in the chat — same audience
    /// rules as status pushes (bound chat, not muted).</summary>
    public async Task EnqueueNotesUpdatedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long shipmentId, string? notes)
    {
        var r = await conn.QuerySingleOrDefaultAsync<(string? ChatId, bool Notifications, string Number, string Pin)>(
            """
            SELECT c.telegram_chat_id::text,
                   c.telegram_notifications,
                   s.tracking_number,
                   s.pin
            FROM shipments s
            JOIN clients c ON c.id = s.recipient_id
            WHERE s.id = @id
            """, new { id = shipmentId });
        if (r.ChatId is null)
        {
            logger.LogInformation("Telegram: notes push SKIPPED for {number} — recipient has no chat id.", r.Number);
            return;
        }
        if (!r.Notifications)
        {
            logger.LogInformation("Telegram: notes push SKIPPED for {number} — recipient muted notifications.", r.Number);
            return;
        }

        // notes can be arbitrarily long; keep the chat message sane.
        var content = notes is null ? null : (notes.Length > 800 ? notes[..797] + "…" : notes);
        var text = notes is null
            ? TelegramTexts.NotesCleared(r.Number)
            : WithAccessCode(TelegramTexts.NotesUpdated(r.Number, content!), r.Number, r.Pin);
        logger.LogInformation("Telegram: notes push for {number} (cleared={cleared}) → chat {chatId}.",
            r.Number, notes is null, r.ChatId);
        await EnqueueAsync(conn, tx, r.ChatId, text, TrackKeyboard(r.Number, r.Pin));
    }

    /// <summary>Pushes about the whole haul to each recipient with their parcels listed.
    /// Every recipient gets a keyboard and access codes for their own parcels only.</summary>
    public async Task EnqueueTripAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string? destinationName,
        Func<string?, IReadOnlyList<string>, string> textTemplate, List<(string ChatId, string Number, string Pin)> parcels)
    {
        foreach (var group in parcels.GroupBy(p => p.ChatId))
        {
            var mine = group.DistinctBy(p => p.Number).ToList();
            var numbers = mine.Select(p => p.Number).ToList();
            var codes = string.Join("\n", mine.Select(p => $"🔑 {p.Number}: {p.Pin}"));
            var text = $"{textTemplate(destinationName, numbers)}\n{codes}";
            await EnqueueAsync(conn, tx, group.Key, text,
                TrackKeyboardGroup(mine.Take(MaxPerMessageButtons).Select(p => (p.Number, (string?)p.Pin)).ToList()));
        }
    }

    /// <summary>Admin/courier alerts — own connection, no shared transaction.</summary>
    public async Task EnqueueAdminAsync(string text)
    {
        var ids = options.AdminChatIdSet();
        if (ids.Count == 0)
        {
            logger.LogInformation("Telegram: admin alert NOT queued (no courier chat ids configured): {preview}", Trunc(text));
            return;
        }

        logger.LogInformation("Telegram: admin alert → {n} courier chat(s) [{ids}].", ids.Count, string.Join(", ", ids));
        await using var conn = await db.OpenAsync();
        foreach (var id in ids)
            await EnqueueAsync(conn, null!, id, text);
    }

    private static string Trunc(string s) => s.Length <= 60 ? s : s[..57] + "…";

    /// <summary>Appends the access code line to a recipient push (the tracking
    /// page's button already carries it, this makes it visible in the text too).</summary>
    private static string WithAccessCode(string text, string number, string? pin)
        => string.IsNullOrWhiteSpace(pin) ? text : $"{text}\n🔑 Kod dostępu do {number}: {pin}";

    /// <summary>One URL button to the tracking page (null when no public URL configured).
    /// Pass the PIN only for recipient pushes.</summary>
    public string? TrackKeyboard(string number, string? pin = null)
    {
        if (string.IsNullOrWhiteSpace(options.SitePublicUrl))
            return null;
        return Keyboard(new[] { (number, pin) }, singleLabel: "📦 Śledź paczkę");
    }

    /// <summary>One URL button per parcel (capped) — used for trip digests.</summary>
    public string? TrackKeyboardGroup(IReadOnlyList<(string Number, string? Pin)> parcels)
    {
        if (string.IsNullOrWhiteSpace(options.SitePublicUrl))
            return null;
        return Keyboard(parcels);
    }

    private string Keyboard(IEnumerable<(string Number, string? Pin)> parcels, string? singleLabel = null)
    {
        var rows = parcels
            .Take(MaxPerMessageButtons)
            .Select(p => new JsonArray(
                new JsonObject { ["text"] = singleLabel ?? p.Number, ["url"] = options.TrackUrl(p.Number, p.Pin) }))
            .Cast<JsonNode>()
            .ToArray();
        var board = new JsonObject { ["inline_keyboard"] = new JsonArray(rows) };
        return board.ToJsonString();
    }
}

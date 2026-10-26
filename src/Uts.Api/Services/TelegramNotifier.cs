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
        logger.LogInformation("Telegram: queued outbox row → chat {chatId}, {len} chars, keyboard={kb}: {preview}",
            chatId, text.Length, keyboardJson != null, Trunc(text));
    }

    /// <summary>Status change push to the recipient (skips missing chat / muted).</summary>
    public async Task EnqueueShipmentStatusAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long shipmentId, string status)
    {
        var r = await conn.QuerySingleOrDefaultAsync<(string? ChatId, bool Notifications, string Number, string? DestinationName)>(
            """
            SELECT c.telegram_chat_id::text,
                   c.telegram_notifications,
                   s.tracking_number,
                   dc.name
            FROM shipments s
            JOIN clients c ON c.id = s.recipient_id
            JOIN cities dc ON dc.code = s.destination_code
            WHERE s.id = @id
            """, new { id = shipmentId });
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

        var text = TelegramTexts.ShipmentStatus(r.Number, status, r.DestinationName);
        logger.LogInformation("Telegram: status push for {number} ({status}) → chat {chatId}.",
            r.Number, status, r.ChatId);
        await EnqueueAsync(conn, tx, r.ChatId, text, TrackKeyboard(r.Number, maxButtons: 1));
    }

    /// <summary>Pushes about the whole haul to each recipient with their parcels listed.</summary>
    public async Task EnqueueTripAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string? destinationName,
        Func<string?, IReadOnlyList<string>, string> textTemplate, List<(string ChatId, string Number)> parcels)
    {
        var keyboardNumbers = parcels.Select(p => p.Number).Distinct().Take(MaxPerMessageButtons).ToList();

        foreach (var group in parcels.GroupBy(p => p.ChatId))
        {
            var numbers = group.Select(p => p.Number).Distinct().ToList();
            await EnqueueAsync(conn, tx, group.Key, textTemplate(destinationName, numbers),
                TrackKeyboardGroup(keyboardNumbers));
        }
    }

    /// <summary>Admin/courier alerts — own connection, no shared transaction.</summary>
    public async Task EnqueueAdminAsync(string text)
    {
        var ids = options.CourierChatIdSet();
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

    /// <summary>One URL button to the tracking page (null when no public URL configured).</summary>
    public string? TrackKeyboard(string number, int maxButtons = 1)
    {
        if (string.IsNullOrWhiteSpace(options.SitePublicUrl))
            return null;
        return Keyboard(new[] { number }, singleLabel: "📦 Śledź paczkę");
    }

    /// <summary>One URL button per parcel (capped) — used for trip digests.</summary>
    public string? TrackKeyboardGroup(IReadOnlyList<string> numbers)
    {
        if (string.IsNullOrWhiteSpace(options.SitePublicUrl))
            return null;
        return Keyboard(numbers);
    }

    private string Keyboard(IEnumerable<string> numbers, string? singleLabel = null)
    {
        var rows = numbers
            .Take(MaxPerMessageButtons)
            .Select(n => new JsonArray(
                new JsonObject { ["text"] = singleLabel ?? n, ["url"] = options.TrackUrl(n) }))
            .Cast<JsonNode>()
            .ToArray();
        var board = new JsonObject { ["inline_keyboard"] = new JsonArray(rows) };
        return board.ToJsonString();
    }
}

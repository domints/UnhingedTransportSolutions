namespace Uts.Api.Services;

/// <summary>Telegram module configuration (env: TELEGRAM__BOT_TOKEN etc.).
/// When BotToken is absent the whole module stays off.</summary>
public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    /// <summary>BotFather token.</summary>
    public string? BotToken { get; set; }

    /// <summary>Public base URL — deep-link buttons point at /track/&lt;number&gt; here.</summary>
    public string? SitePublicUrl { get; set; }

    /// <summary>Bot username (without @). Prefilled from getMe at startup.</summary>
    public string? BotUsername { get; set; }

    /// <summary>Chat IDs that may run courier commands, comma-separated.</summary>
    public string? CourierChatIds { get; set; }

    public IReadOnlySet<string> CourierChatIdSet()
        => (CourierChatIds ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet();

    public string TrackUrl(string number) => $"{(SitePublicUrl?.TrimEnd('/') ?? "")}/track/{number}";

    public string ClaimUrl(string code) => BotUsername is null ? "" : $"https://t.me/{BotUsername}?start={code}";
}

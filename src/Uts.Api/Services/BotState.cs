namespace Uts.Api.Services;

/// <summary>Runtime state shared between the bot service and endpoints
/// (the username is only known after the first getMe succeeds).</summary>
public sealed class BotState
{
    public string? BotUsername { get; set; }
    public bool Enabled => !string.IsNullOrEmpty(BotUsername);
}

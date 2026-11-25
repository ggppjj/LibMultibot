namespace LibMultibot.Interfaces;

/// <summary>A message a platform received, in a form that does not depend on the platform.</summary>
/// <param name="AuthorUsername">The author's unique account name.</param>
/// <param name="AuthorDisplayName">The name shown for the author where the message was sent.</param>
/// <param name="GuildId">Null for a direct message.</param>
/// <param name="BotUserId">The receiving bot's own user ID, or 0 if not known yet.</param>
public sealed record BotMessage(
    ulong AuthorId,
    string AuthorUsername,
    string AuthorDisplayName,
    ulong ChannelId,
    ulong? GuildId,
    string Content,
    ulong BotUserId
)
{
    public bool IsDirectMessage => GuildId is null;
}

/// <summary>
/// Sees every message from a human that the bot can read, including direct messages, and
/// answers through the platform it is handed. Messages can arrive concurrently.
/// </summary>
public interface IBotMessageListener
{
    string Name { get; }
    bool IsActive { get; }
    Task OnMessage(BotMessage message, IBotPlatform platform);
}

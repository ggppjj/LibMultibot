using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LibMultibot.Helper_Classes;
using Serilog;

namespace LibMultibot.Platforms;

/// <summary>
/// One reaction-driven channel-visibility toggle. When a user reacts to
/// <see cref="MessageId"/> with <see cref="Emoji"/>, the bot adds a per-user
/// permission overwrite on <see cref="TargetChannelId"/> that hides it from
/// them. Removing the reaction restores visibility (deletes the overwrite).
/// </summary>
public class ReactionChannelToggle
{
    /// <summary>The message users react to (the explainer message).</summary>
    public ulong MessageId { get; set; }

    /// <summary>
    /// The channel that holds <see cref="MessageId"/>. Required so the bot can
    /// fetch reactions on startup and process any added/removed while it was down.
    /// </summary>
    public ulong MessageChannelId { get; set; }

    /// <summary>
    /// Unicode emoji (e.g. "🙈") or the name of a custom guild emoji (without
    /// colons). Matched against the reaction's emoji name.
    /// </summary>
    public string Emoji { get; set; } = string.Empty;

    /// <summary>The channel whose visibility gets toggled for the reacting user.</summary>
    public ulong TargetChannelId { get; set; }
}

public class ModerationConfig
{
    /// <summary>
    /// Channels that act as spambot honeypots. Any non-exempt human who posts
    /// in one is banned immediately.
    /// </summary>
    public List<ulong> HoneypotChannelIds { get; set; } = [];

    /// <summary>
    /// Seconds of the banned user's recent message history Discord purges
    /// guild-wide on ban. 3600 = last hour. Max 604800 (7 days).
    /// </summary>
    public int HoneypotBanDeleteMessageSeconds { get; set; } = 3600;

    /// <summary>User IDs never banned by the honeypot (e.g. admins, mods).</summary>
    public List<ulong> HoneypotExemptUserIds { get; set; } = [];

    /// <summary>Reaction-driven channel-visibility toggles.</summary>
    public List<ReactionChannelToggle> ReactionChannelToggles { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ModerationConfig))]
internal partial class ModerationConfigJsonContext : JsonSerializerContext { }

internal class ModerationCommandConfig(string botName, string commandName, ILogger logger)
    : CommandConfigBase<ModerationConfig>(botName, commandName, logger)
{
    protected override JsonSerializerContext JsonContext => ModerationConfigJsonContext.Default;
    protected override JsonTypeInfo<ModerationConfig> JsonTypeInfo =>
        ModerationConfigJsonContext.Default.ModerationConfig;

    protected override ModerationConfig CreateDefaultConfig() =>
        new()
        {
            HoneypotChannelIds = [1518310706049454211], // honeypot channel
            HoneypotExemptUserIds = [142829604330143744], // ggppjj
            ReactionChannelToggles =
            [
                new ReactionChannelToggle
                {
                    MessageId = 1518310530744324156, // explainer message
                    MessageChannelId = 898653215371968532, // channel holding the explainer
                    Emoji = "👍",
                    TargetChannelId = 1518310706049454211, // hide honeypot
                },
            ],
        };
}

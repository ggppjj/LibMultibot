using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LibMultibot.Helper_Classes;
using Serilog;

namespace LibMultibot.Platforms;

/// <summary>
/// A single, self-contained honeypot. Any non-exempt human who posts in
/// <see cref="ChannelId"/> is banned. Optionally exposes an opt-out: reacting to
/// <see cref="OptOutMessageId"/> with <see cref="OptOutEmoji"/> hides this
/// honeypot channel from the reacting user (removing the reaction unhides it).
/// Each entry is independent, so multiple honeypots can coexist.
/// </summary>
public class Honeypot
{
    /// <summary>The trap channel. Posting here (non-exempt) triggers a ban.</summary>
    public ulong ChannelId { get; set; }

    /// <summary>
    /// Seconds of the banned user's recent message history Discord purges
    /// guild-wide on ban. 3600 = last hour. Max 604800 (7 days).
    /// </summary>
    public int BanDeleteMessageSeconds { get; set; } = 3600;

    /// <summary>User IDs never banned by this honeypot (e.g. admins, mods).</summary>
    public List<ulong> ExemptUserIds { get; set; } = [];

    /// <summary>
    /// Opt-out message users react to to hide this honeypot. 0 = no opt-out.
    /// </summary>
    public ulong OptOutMessageId { get; set; }

    /// <summary>
    /// Channel holding <see cref="OptOutMessageId"/>. Required so the bot can fetch
    /// reactions on startup and process any added/removed while it was down.
    /// </summary>
    public ulong OptOutMessageChannelId { get; set; }

    /// <summary>
    /// Unicode emoji (e.g. "🙈") or the name of a custom guild emoji (without
    /// colons) that toggles the opt-out. "*" = any reaction counts as opt-out.
    /// Empty = no opt-out.
    /// </summary>
    public string OptOutEmoji { get; set; } = string.Empty;

    /// <summary>True when this honeypot has a usable opt-out reaction configured.</summary>
    [JsonIgnore]
    public bool HasOptOut =>
        OptOutMessageId != 0 && OptOutMessageChannelId != 0 && !string.IsNullOrEmpty(OptOutEmoji);
}

public class ModerationConfig
{
    /// <summary>Independent honeypots, each with its own channel, ban window,
    /// exempt list, and optional opt-out reaction.</summary>
    public List<Honeypot> Honeypots { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ModerationConfig))]
[JsonSerializable(typeof(Honeypot))]
internal partial class ModerationConfigJsonContext : JsonSerializerContext { }

internal class ModerationCommandConfig(string botName, string commandName, ILogger logger)
    : CommandConfigBase<ModerationConfig>(botName, commandName, logger)
{
    protected override JsonSerializerContext JsonContext => ModerationConfigJsonContext.Default;
    protected override JsonTypeInfo<ModerationConfig> JsonTypeInfo =>
        ModerationConfigJsonContext.Default.ModerationConfig;

    /// <summary>Raised after the config file is (re)loaded, including runtime edits.</summary>
    public event Action? Reloaded;

    protected override void OnConfigLoaded() => Reloaded?.Invoke();

    protected override ModerationConfig CreateDefaultConfig() =>
        new()
        {
            Honeypots =
            [
                new Honeypot
                {
                    ChannelId = 1518310706049454211, // honeypot channel
                    ExemptUserIds = [142829604330143744], // ggppjj
                    OptOutMessageId = 1518310530744324156, // explainer message
                    OptOutMessageChannelId = 898653215371968532, // channel holding it
                    OptOutEmoji = "👍",
                },
            ],
        };
}

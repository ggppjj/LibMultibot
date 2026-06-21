using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LibMultibot.Helper_Classes;
using LibMultibot.Interfaces;
using Microsoft.Extensions.Configuration;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using Serilog;

namespace LibMultibot.Platforms;

[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class TokenJsonContext : JsonSerializerContext { }

public delegate void CommandEventHandler(object? sender, EventArgs? e);

public class DiscordPlatform : IBotPlatform
{
    public string Name { get; } = "Discord";
    public IBot Bot { get; }
    public List<IBotCommand> Commands { get; } = [];

    private readonly string _tokenFilePath = Path.Combine(
        AppContext.BaseDirectory,
        "DiscordTokens.json"
    );
    private readonly IConfiguration _tokenConfig;
    private readonly GatewayClient _client;
    private readonly ApplicationCommandService<SlashCommandContext> _commandService;
    private readonly ILogger _logger;
    private readonly string? _token;
    public bool IsActive { get; set; } = true;
    private readonly List<ulong> _trackedMessages = [];
    private readonly ModerationCommandConfig _moderationConfig;

    public event CommandEventHandler? OnCommand;

    public DiscordPlatform(IBot bot)
    {
        Bot = bot;
        _logger = LogController.SetupLogging(typeof(DiscordPlatform));
        _logger.Information($"Starting for {Bot.Name}...");

        if (!File.Exists(_tokenFilePath))
        {
            var template = new Dictionary<string, string>
            {
                { bot.Name, "YOUR_DISCORD_BOT_TOKEN_HERE" },
            };
            File.WriteAllText(
                _tokenFilePath,
                JsonSerializer.Serialize(template, TokenJsonContext.Default.DictionaryStringString)
            );
            _logger.Warning(
                $"Created {_tokenFilePath} with a placeholder token. Please update it."
            );
        }

        _tokenConfig = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("DiscordTokens.json", optional: true, reloadOnChange: true)
            .Build();

        _token = _tokenConfig[Bot.Name];
        if (string.IsNullOrWhiteSpace(_token) || _token == "YOUR_DISCORD_BOT_TOKEN_HERE")
        {
            _logger.Fatal("Missing bot token in DiscordTokens.json file!");
            throw new InvalidDataException("Missing bot token!");
        }

        _client = new GatewayClient(
            new BotToken(_token),
            new GatewayClientConfiguration
            {
                Intents =
                    GatewayIntents.Guilds
                    | GatewayIntents.GuildMessages
                    | GatewayIntents.MessageContent
                    | GatewayIntents.GuildMessageReactions,
            }
        );

        _moderationConfig = new ModerationCommandConfig(Bot.Name, "Moderation", _logger);

        _commandService = new ApplicationCommandService<SlashCommandContext>();

        _client.InteractionCreate += async interaction => await HandleInteractionAsync(interaction);
        _client.MessageCreate += async message => await HandleMessageAsync(message);
        _client.MessageReactionAdd += async args =>
            await HandleReactionAsync(args.GuildId, args.MessageId, args.UserId, args.Emoji.Name, hide: true);
        _client.MessageReactionRemove += async args =>
            await HandleReactionAsync(args.GuildId, args.MessageId, args.UserId, args.Emoji.Name, hide: false);
        _client.Ready += async args => await OnClientReady(args);

        LoadCommands();
    }

    public async Task StartAsync()
    {
        if (string.IsNullOrWhiteSpace(_token))
        {
            _logger.Fatal("Missing bot token in DiscordTokens.json file!");
            throw new InvalidDataException("Missing bot token!");
        }
        await _client.StartAsync();
        _logger.Information($"Started for {Bot.Name}.");
    }

    public async Task Shutdown()
    {
        _logger.Information($"Shutting down for {Bot.Name}...");
        await _client.CloseAsync();
        _logger.Information($"Shutdown for {Bot.Name} complete.");
    }

    private async Task HandleInteractionAsync(Interaction interaction)
    {
        if (interaction is not SlashCommandInteraction slashCommand || !IsActive)
            return;

        try
        {
            var matchingCommand = Commands
                .Where(c => c.CommandType.HasFlag(BotCommandTypes.SlashCommand))
                .FirstOrDefault(c =>
                    c.Name.Equals(
                        slashCommand.Data.Name,
                        StringComparison.InvariantCultureIgnoreCase
                    )
                );

            if (matchingCommand is null)
            {
                await interaction.SendResponseAsync(
                    InteractionCallback.Message(
                        new InteractionMessageProperties
                        {
                            Content = "Unknown command.",
                            Flags = MessageFlags.Ephemeral,
                        }
                    )
                );
                return;
            }

            if (!matchingCommand.IsInitialized)
            {
                await interaction.SendResponseAsync(
                    InteractionCallback.Message(
                        new InteractionMessageProperties
                        {
                            Content = "This command is not yet ready for use.",
                            Flags = MessageFlags.Ephemeral,
                        }
                    )
                );
                return;
            }

            await interaction.SendResponseAsync(InteractionCallback.DeferredMessage());

            var response = await matchingCommand.Response.PrepareResponse();

            if (!response)
            {
                await interaction.SendFollowupMessageAsync(
                    new InteractionMessageProperties
                    {
                        Content = "No response from command.",
                        Flags = MessageFlags.Ephemeral,
                    }
                );
                return;
            }
            var messageProps = new InteractionMessageProperties
            {
                Content = matchingCommand.Response.Message,
            };
            if (
                !string.IsNullOrEmpty(matchingCommand.Response.EmbedTitle)
                || !string.IsNullOrEmpty(matchingCommand.Response.EmbedDescription)
            )
            {
                Color color = new(0);
                if (matchingCommand.Response.EmbedColor != null)
                    color = new(
                        matchingCommand.Response.EmbedColor.Value.R,
                        matchingCommand.Response.EmbedColor.Value.G,
                        matchingCommand.Response.EmbedColor.Value.B
                    );

                var embed = new EmbedProperties()
                    .WithDescription(matchingCommand.Response.EmbedDescription ?? string.Empty)
                    .WithColor(color);

                if (matchingCommand.Response.EmbedTitle != null)
                    embed = embed.WithTitle(matchingCommand.Response.EmbedTitle);

                if (!string.IsNullOrEmpty(matchingCommand.Response.EmbedFileName))
                {
                    embed.Image = new EmbedImageProperties(
                        $"attachment://{matchingCommand.Response.EmbedFileName}"
                    );
                }

                messageProps.AddEmbeds(embed);
            }

            if (
                !string.IsNullOrEmpty(matchingCommand.Response.EmbedFilePath)
                && !string.IsNullOrEmpty(matchingCommand.Response.EmbedFileName)
            )
            {
                await using var fileStream = File.OpenRead(matchingCommand.Response.EmbedFilePath);
                var attachment = new AttachmentProperties(
                    matchingCommand.Response.EmbedFileName,
                    fileStream
                );
                messageProps.AddAttachments(attachment);
                await interaction.SendFollowupMessageAsync(messageProps);
            }
            else
            {
                await interaction.SendFollowupMessageAsync(messageProps);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Error processing slash command '{slashCommand.Data.Name}'.");
            try
            {
                try
                {
                    await interaction.SendFollowupMessageAsync(
                        new InteractionMessageProperties
                        {
                            Content = "An error occurred while processing your command.",
                            Flags = MessageFlags.Ephemeral,
                        }
                    );
                }
                catch
                {
                    try
                    {
                        await interaction.SendResponseAsync(
                            InteractionCallback.Message(
                                new InteractionMessageProperties
                                {
                                    Content = "An error occurred while processing your command.",
                                    Flags = MessageFlags.Ephemeral,
                                }
                            )
                        );
                    }
                    catch (Exception followupEx)
                    {
                        _logger.Error(followupEx, "Failed to send error response.");
                    }
                }
            }
            catch (Exception followupEx)
            {
                _logger.Error(followupEx, "Failed to send error response.");
            }
        }
    }

    public async Task SendMessage(string message, ulong? channelId, bool trackedMessage = false)
    {
        if (channelId == null)
            return;
        var sentMessage = await _client.Rest.SendMessageAsync(channelId.Value, message);
        if (trackedMessage)
            _trackedMessages.Add(sentMessage.Id);
    }

    private async Task HandleMessageAsync(Message message)
    {
        if (message.Author.IsBot || !IsActive)
            return;

        try
        {
            if (
                message.GuildId != null
                && _moderationConfig.Config.HoneypotChannelIds.Contains(message.ChannelId)
            )
            {
                if (_moderationConfig.Config.HoneypotExemptUserIds.Contains(message.Author.Id))
                {
                    _logger.Warning(
                        "Honeypot ACTIVE: exempt user {Username} ({UserId}) posted in honeypot channel {ChannelId} — skipping ban (whitelisted).",
                        message.Author.Username,
                        message.Author.Id,
                        message.ChannelId
                    );
                    return;
                }

                await _client.Rest.BanGuildUserAsync(
                    message.GuildId.Value,
                    message.Author.Id,
                    _moderationConfig.Config.HoneypotBanDeleteMessageSeconds,
                    new RestRequestProperties { AuditLogReason = "Auto-ban: posted in honeypot channel." }
                );
                _logger.Warning(
                    "Banned {Username} ({UserId}) for posting in honeypot channel {ChannelId}; purged last {Seconds}s of messages.",
                    message.Author.Username,
                    message.Author.Id,
                    message.ChannelId,
                    _moderationConfig.Config.HoneypotBanDeleteMessageSeconds
                );
                return;
            }

            var inReplyTo = message.MessageReference?.MessageId;
            if (
                inReplyTo != null
                && message.GuildId != null
                && _trackedMessages.Contains(inReplyTo.Value)
            )
            {
                var guildUser = await _client.Rest.GetGuildUserAsync(
                    message.GuildId.Value,
                    message.Author.Id
                );
                await guildUser.TimeOutAsync(DateTime.Now.AddMinutes(5));
                _logger.Warning(
                    "Timed out {Username} ({UserId}) for replying to a tracked message.",
                    message.Author.Username,
                    message.Author.Id
                );
            }

            if (!message.Content.StartsWith('!'))
                return;

            var commandText = new string(
                message.Content[1..].Split(' ')[0].Where(c => !char.IsPunctuation(c)).ToArray()
            ).ToLowerInvariant();

            var matchingCommand = Commands
                .Where(c => c.CommandType.HasFlag(BotCommandTypes.TextCommand))
                .FirstOrDefault(c =>
                    c.Name.Equals(commandText, StringComparison.InvariantCultureIgnoreCase)
                );

            if (matchingCommand is null)
                return;

            if (!matchingCommand.IsInitialized)
            {
                await message.ReplyAsync("Command loading, please wait...");
                return;
            }

            if (
                matchingCommand.RestrictedToChannelIDs?.Count > 0
                && !matchingCommand.RestrictedToChannelIDs.Contains(message.ChannelId)
            )
            {
                var author = message.Author;
                var server = message.Guild?.Name;

                await message.DeleteAsync();
                var dm = await author.GetDMChannelAsync();
                await dm.SendMessageAsync(
                    @$"Whoops! 
                    
                    You tried to send a restricted command in the wrong channel in {(server ?? "some server that I couldn't figure out or something, let ggppjj know if you see this")}. 
                    ""{message.Content}"". 
                    You can only send that in channel ID(s) {String.Join(',', matchingCommand.RestrictedToChannelIDs?.Select(i => i.ToString()) ??
                            [
                                "Unknown!",
                            ])}."
                );
                return;
            }

            if (
                matchingCommand.IsAdminCommand
                && (!matchingCommand.AdminUsers?.Any(i => i.Id == message.Author.Id) ?? false)
            )
            {
                return;
            }

            matchingCommand.MessageContext = message.Content;
            matchingCommand.MessageAuthorId = message.Author.Id;
            var response = await matchingCommand.Response.PrepareResponse();

            if (!response)
            {
                await message.ReplyAsync("No response from command.");
                return;
            }

            var messageProps = new ReplyMessageProperties();
            if (!string.IsNullOrEmpty(matchingCommand.Response.Message))
                messageProps.Content = matchingCommand.Response.Message;

            if (
                !string.IsNullOrEmpty(matchingCommand.Response.EmbedTitle)
                || !string.IsNullOrEmpty(matchingCommand.Response.EmbedDescription)
            )
            {
                Color color = new(0);
                if (matchingCommand.Response.EmbedColor != null)
                    color = new(
                        matchingCommand.Response.EmbedColor.Value.R,
                        matchingCommand.Response.EmbedColor.Value.G,
                        matchingCommand.Response.EmbedColor.Value.B
                    );

                var embed = new EmbedProperties()
                    .WithTitle(matchingCommand.Response.EmbedTitle ?? string.Empty)
                    .WithDescription(matchingCommand.Response.EmbedDescription ?? string.Empty)
                    .WithColor(color);
                if (!string.IsNullOrEmpty(matchingCommand.Response.EmbedFileName))
                {
                    embed.Image = new EmbedImageProperties(
                        $"attachment://{matchingCommand.Response.EmbedFileName}"
                    );
                }
                messageProps.AddEmbeds(embed);
            }

            if (
                !string.IsNullOrEmpty(matchingCommand.Response.EmbedFilePath)
                && !string.IsNullOrEmpty(matchingCommand.Response.EmbedFileName)
            )
            {
                using var fileStream = File.OpenRead(matchingCommand.Response.EmbedFilePath);
                var attachment = new AttachmentProperties(
                    matchingCommand.Response.EmbedFileName,
                    fileStream
                );
                messageProps.AddAttachments(attachment);
                await message.ReplyAsync(messageProps);
            }
            else
            {
                await message.ReplyAsync(messageProps);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Error processing text command from message: {message.Content}.");
            try
            {
                await message.ReplyAsync("An error occurred while processing your command.");
            }
            catch (Exception replyEx)
            {
                _logger.Error(replyEx, "Failed to send error response.");
            }
        }
    }

    private async Task HandleReactionAsync(
        ulong? guildId,
        ulong messageId,
        ulong userId,
        string? emojiName,
        bool hide
    )
    {
        if (guildId == null || !IsActive)
            return;

        var toggle = _moderationConfig.Config.ReactionChannelToggles.FirstOrDefault(t =>
            t.MessageId == messageId
            && string.Equals(t.Emoji, emojiName, StringComparison.Ordinal)
        );

        if (toggle == null)
            return;

        if (hide)
            await HideChannelForUserAsync(toggle.TargetChannelId, userId);
        else
            await RestoreChannelForUserAsync(toggle.TargetChannelId, userId);
    }

    // Add a per-user overwrite denying ViewChannel -> channel disappears for them.
    private async Task HideChannelForUserAsync(ulong channelId, ulong userId)
    {
        try
        {
            await _client.Rest.ModifyGuildChannelPermissionsAsync(
                channelId,
                new PermissionOverwriteProperties(userId, PermissionOverwriteType.User)
                {
                    Denied = Permissions.ViewChannel,
                }
            );
            _logger.Information("Hid channel {ChannelId} for user {UserId}.", channelId, userId);
        }
        catch (Exception ex)
        {
            _logger.Error(
                ex,
                "Failed to hide channel {ChannelId} for user {UserId}.",
                channelId,
                userId
            );
        }
    }

    // Drop the per-user overwrite, restoring default visibility.
    private async Task RestoreChannelForUserAsync(ulong channelId, ulong userId)
    {
        try
        {
            await _client.Rest.DeleteGuildChannelPermissionAsync(channelId, userId);
            _logger.Information(
                "Restored channel {ChannelId} visibility for user {UserId}.",
                channelId,
                userId
            );
        }
        catch (Exception ex)
        {
            _logger.Error(
                ex,
                "Failed to restore channel {ChannelId} for user {UserId}.",
                channelId,
                userId
            );
        }
    }

    // Reactions added/removed while the bot was offline never arrive as gateway
    // events. On startup, fetch each toggle message's current reactors and bring
    // the target channel's per-user overwrites in line with them: hide for anyone
    // currently reacting, restore anyone who is no longer reacting.
    private async Task ReconcileReactionTogglesAsync()
    {
        foreach (var toggle in _moderationConfig.Config.ReactionChannelToggles)
        {
            if (
                toggle.MessageId == 0
                || toggle.MessageChannelId == 0
                || toggle.TargetChannelId == 0
                || string.IsNullOrEmpty(toggle.Emoji)
            )
                continue;

            try
            {
                // Custom emoji need an ID we don't store; only unicode is reconcilable.
                var emoji = new ReactionEmojiProperties(toggle.Emoji);

                var reacted = new HashSet<ulong>();
                await foreach (
                    var user in _client.Rest.GetMessageReactionsAsync(
                        toggle.MessageChannelId,
                        toggle.MessageId,
                        emoji
                    )
                )
                {
                    if (!user.IsBot)
                        reacted.Add(user.Id);
                }

                // Existing per-user "hide" overwrites we previously wrote (deny == ViewChannel only).
                var hiddenNow = new HashSet<ulong>();
                if (
                    await _client.Rest.GetChannelAsync(toggle.TargetChannelId)
                    is IGuildChannel guildChannel
                )
                {
                    foreach (var (id, overwrite) in guildChannel.PermissionOverwrites)
                    {
                        if (
                            overwrite.Type == PermissionOverwriteType.User
                            && overwrite.Denied == Permissions.ViewChannel
                            && overwrite.Allowed == default
                        )
                            hiddenNow.Add(id);
                    }
                }

                foreach (var userId in reacted.Where(u => !hiddenNow.Contains(u)))
                {
                    _logger.Information(
                        "Backlog: applying hide for user {UserId} who reacted while offline.",
                        userId
                    );
                    await HideChannelForUserAsync(toggle.TargetChannelId, userId);
                }

                foreach (var userId in hiddenNow.Where(u => !reacted.Contains(u)))
                {
                    _logger.Information(
                        "Backlog: restoring user {UserId} who removed their reaction while offline.",
                        userId
                    );
                    await RestoreChannelForUserAsync(toggle.TargetChannelId, userId);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(
                    ex,
                    "Failed to reconcile reaction toggle on message {MessageId}.",
                    toggle.MessageId
                );
            }
        }
    }

    private async Task OnClientReady(ReadyEventArgs args)
    {
        try
        {
            var existingCommands = await _client.Rest.GetGlobalApplicationCommandsAsync(
                args.User.Id
            );

            var desiredCommands = new List<ApplicationCommandProperties>();
            foreach (
                var command in Commands.Where(c =>
                    c.CommandType.HasFlag(BotCommandTypes.SlashCommand)
                )
            )
            {
                desiredCommands.Add(
                    new SlashCommandProperties(command.Name.ToLowerInvariant(), command.Description)
                );
            }

            bool needsUpdate = CommandsHaveChanged(existingCommands, desiredCommands);

            if (needsUpdate)
            {
                _logger.Information("Commands have changed, updating registration...");
                await _client.Rest.BulkOverwriteGlobalApplicationCommandsAsync(
                    args.User.Id,
                    desiredCommands
                );
                _logger.Information("Commands registered successfully.");
            }
            else
            {
                _logger.Information("Commands are up to date, skipping registration.");
            }

            await ReconcileReactionTogglesAsync();

            _logger.Information("Ready!");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error during ready event.");
        }
    }

    private static bool CommandsHaveChanged(
        IEnumerable<ApplicationCommand> existing,
        List<ApplicationCommandProperties> desired
    )
    {
        var existingList = existing.ToList();

        if (existingList.Count != desired.Count)
            return true;

        foreach (var desiredCmd in desired)
        {
            if (desiredCmd is not SlashCommandProperties slashCmd)
                continue;

            var existingCmd = existingList.FirstOrDefault(e =>
                e.Name.Equals(slashCmd.Name, StringComparison.OrdinalIgnoreCase)
            );

            if (existingCmd == null || existingCmd.Description != slashCmd.Description)
                return true;
        }

        return false;
    }

    private void LoadCommands()
    {
        foreach (var botCommand in Bot.Commands)
        {
            if (botCommand.CommandPlatforms.Contains(BotPlatforms.Discord))
                Commands.Add(botCommand);
        }
    }

    protected virtual void RaiseCommandEvent(EventArgs e) => OnCommand?.Invoke(this, e);
}

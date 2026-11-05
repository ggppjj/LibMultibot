using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<ulong, byte> _trackedMessages = new();

    // Gateway events are handled concurrently, but a command keeps its request and response
    // in its own properties, so each command handles one request at a time.
    private readonly ConcurrentDictionary<IBotCommand, SemaphoreSlim> _commandLocks = new();
    private readonly ModerationCommandConfig _moderationConfig;
    private readonly BanRestoreStore _banRestoreStore;
    private readonly HideSkipStore _hideSkipStore;
    private volatile bool _isReady;

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
                    | GatewayIntents.GuildMessageReactions
                    | GatewayIntents.GuildUsers,
            }
        );

        _moderationConfig = new ModerationCommandConfig(Bot.Name, "Moderation", _logger);
        _banRestoreStore = new BanRestoreStore(Bot.Name, _logger);
        _hideSkipStore = new HideSkipStore(Bot.Name, _logger);
        // Runtime config edits (e.g. re-pointing an opt-out message/channel) should
        // re-scan reactions, just like startup. Live ban/reaction matching already
        // reads the latest config per event, so only the reconcile needs nudging.
        _moderationConfig.Reloaded += () =>
        {
            if (_isReady)
                _ = ReconcileReactionTogglesAsync();
        };

        _commandService = new ApplicationCommandService<SlashCommandContext>();

        _client.InteractionCreate += async interaction => await HandleInteractionAsync(interaction);
        _client.MessageCreate += async message => await HandleMessageAsync(message);
        _client.MessageReactionAdd += async args =>
        {
            // Reconciling ignores bots, and would undo a hide applied to one here.
            if (args.User?.IsBot == true)
                return;
            await HandleReactionAsync(args.GuildId, args.MessageId, args.UserId, args.Emoji.Name, hide: true);
        };
        _client.MessageReactionRemove += async args =>
            await HandleReactionAsync(args.GuildId, args.MessageId, args.UserId, args.Emoji.Name, hide: false);
        // Mod bulk removals (clear all reactions / clear one emoji) don't fire
        // per-user remove events; reconcile the affected message instead.
        _client.MessageReactionRemoveAll += async args =>
            await ReconcileByOptOutMessageAsync(args.MessageId);
        _client.MessageReactionRemoveEmoji += async args =>
            await ReconcileByOptOutMessageAsync(args.MessageId);
        _client.GuildUserAdd += async member => await HandleGuildUserAddAsync(member);
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

    private static bool IsAdmin(IBotCommand command, ulong userId) =>
        command.AdminUsers?.Any(u => u.Id == userId) == true;

    private async Task<CommandLockHold> HoldCommandAsync(IBotCommand command)
    {
        var commandLock = _commandLocks.GetOrAdd(command, _ => new SemaphoreSlim(1, 1));
        await commandLock.WaitAsync();
        return new CommandLockHold(commandLock);
    }

    private sealed class CommandLockHold(SemaphoreSlim commandLock) : IDisposable
    {
        public void Dispose() => commandLock.Release();
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

            var notAllowed =
                matchingCommand.IsAdminCommand && !IsAdmin(matchingCommand, interaction.User.Id);
            var wrongChannel =
                matchingCommand.RestrictedToChannelIDs?.Count > 0
                && !matchingCommand.RestrictedToChannelIDs.Contains(interaction.Channel.Id);
            if (notAllowed || wrongChannel)
            {
                await interaction.SendResponseAsync(
                    InteractionCallback.Message(
                        new InteractionMessageProperties
                        {
                            Content = notAllowed
                                ? "You can't use this command."
                                : "This command can't be used in this channel.",
                            Flags = MessageFlags.Ephemeral,
                        }
                    )
                );
                return;
            }

            await interaction.SendResponseAsync(InteractionCallback.DeferredMessage());

            using var commandHold = await HoldCommandAsync(matchingCommand);

            // Slash commands carry no text. Clear it, or the command would act on the
            // arguments of the last text command it received.
            matchingCommand.MessageContext = null;
            matchingCommand.MessageAuthorId = interaction.User.Id;
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
            _trackedMessages[sentMessage.Id] = 0;
    }

    public async Task SendEmbed(
        ulong channelId,
        string? title,
        string? description,
        System.Drawing.Color? color = null,
        string? filePath = null,
        string? fileName = null
    )
    {
        var embed = new EmbedProperties()
            .WithTitle(title ?? string.Empty)
            .WithDescription(description ?? string.Empty);
        if (color is { } c)
            embed = embed.WithColor(new Color(c.R, c.G, c.B));

        var hasFile = !string.IsNullOrEmpty(filePath) && !string.IsNullOrEmpty(fileName);
        // Stays open until the send completes; the attachment reads from it.
        await using var fileStream = hasFile ? File.OpenRead(filePath!) : null;

        var messageProps = new MessageProperties();
        if (hasFile)
        {
            embed.Image = new EmbedImageProperties($"attachment://{fileName}");
            messageProps.AddAttachments(new AttachmentProperties(fileName!, fileStream!));
        }
        messageProps.AddEmbeds(embed);

        await _client.Rest.SendMessageAsync(channelId, messageProps);
    }

    private async Task HandleMessageAsync(Message message)
    {
        if (message.Author.IsBot || !IsActive)
            return;

        try
        {
            var honeypot = _moderationConfig.Config.Honeypots.FirstOrDefault(h =>
                h.ChannelId == message.ChannelId
            );
            if (message.GuildId != null && honeypot != null)
            {
                if (honeypot.ExemptUserIds.Contains(message.Author.Id))
                {
                    _logger.Warning(
                        "Honeypot ACTIVE: exempt user {Username} ({UserId}) posted in honeypot channel {ChannelId}: skipping ban (whitelisted).",
                        message.Author.Username,
                        message.Author.Id,
                        message.ChannelId
                    );
                    return;
                }

                // Snapshot roles + nick before banning so they can be reapplied if
                // the user is later unbanned and rejoins.
                try
                {
                    var member = await _client.Rest.GetGuildUserAsync(
                        message.GuildId.Value,
                        message.Author.Id
                    );
                    _banRestoreStore.Add(
                        new BanRestoreRecord
                        {
                            GuildId = message.GuildId.Value,
                            UserId = message.Author.Id,
                            // RoleIds excludes @everyone (its id == the guild id).
                            RoleIds = member
                                .RoleIds.Where(id => id != message.GuildId.Value)
                                .ToList(),
                            Nickname = member.Nickname,
                            BannedAt = DateTimeOffset.UtcNow,
                        }
                    );
                }
                catch (Exception ex)
                {
                    _logger.Warning(
                        ex,
                        "Couldn't snapshot roles/nick before banning {UserId}; restore unavailable.",
                        message.Author.Id
                    );
                }

                // Discord rejects a ban outside this range, so a bad config value can't stop one.
                var deleteSeconds = Math.Clamp(honeypot.BanDeleteMessageSeconds, 0, 604800);
                try
                {
                    await _client.Rest.BanGuildUserAsync(
                        message.GuildId.Value,
                        message.Author.Id,
                        deleteSeconds,
                        new RestRequestProperties { AuditLogReason = "Auto-ban: posted in honeypot channel." }
                    );
                }
                catch (Exception ex)
                {
                    // Not banned, so the snapshot must not restore roles if they ever rejoin.
                    _banRestoreStore.TryTake(message.GuildId.Value, message.Author.Id);
                    _logger.Error(
                        ex,
                        "Failed to ban {Username} ({UserId}) for posting in honeypot channel {ChannelId}.",
                        message.Author.Username,
                        message.Author.Id,
                        message.ChannelId
                    );
                    return;
                }
                _logger.Warning(
                    "Banned {Username} ({UserId}) for posting in honeypot channel {ChannelId}; purged last {Seconds}s of messages.",
                    message.Author.Username,
                    message.Author.Id,
                    message.ChannelId,
                    deleteSeconds
                );
                return;
            }

            var inReplyTo = message.MessageReference?.MessageId;
            if (
                inReplyTo != null
                && message.GuildId != null
                && _trackedMessages.ContainsKey(inReplyTo.Value)
            )
            {
                // Some users can't be timed out (e.g. the owner), which must not stop the
                // rest of this message from being handled.
                try
                {
                    var guildUser = await _client.Rest.GetGuildUserAsync(
                        message.GuildId.Value,
                        message.Author.Id
                    );
                    await guildUser.TimeOutAsync(DateTimeOffset.UtcNow.AddMinutes(5));
                    _logger.Warning(
                        "Timed out {Username} ({UserId}) for replying to a tracked message.",
                        message.Author.Username,
                        message.Author.Id
                    );
                }
                catch (Exception ex)
                {
                    _logger.Warning(
                        ex,
                        "Couldn't time out {Username} ({UserId}) for replying to a tracked message.",
                        message.Author.Username,
                        message.Author.Id
                    );
                }
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
                // Users can block DMs. The message is gone by now, so the general error
                // reply below could not be sent either.
                try
                {
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
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Couldn't send the wrong channel DM to {UserId}.", author.Id);
                }
                return;
            }

            if (matchingCommand.IsAdminCommand && !IsAdmin(matchingCommand, message.Author.Id))
            {
                return;
            }

            using var commandHold = await HoldCommandAsync(matchingCommand);

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

        var honeypot = _moderationConfig.Config.Honeypots.FirstOrDefault(h =>
            h.HasOptOut && h.OptOutMessageId == messageId && OptOutEmojiMatches(h, emojiName)
        );

        if (honeypot == null)
            return;

        if (hide)
        {
            await HideChannelForUserAsync(honeypot.ChannelId, userId);
            return;
        }

        // Reaction removed. Under "*", a user may have stacked several reactions;
        // only unhide once their LAST opt-out reaction is gone. Otherwise removing
        // any one would let them peek while still "opted out".
        if (honeypot.OptOutEmoji == AnyEmojiWildcard && await UserHasOptOutReactionAsync(honeypot, userId))
            return;

        await RestoreChannelForUserAsync(honeypot.ChannelId, userId);
    }

    // A previously-banned user rejoined (after being unbanned). If we snapshotted
    // their roles/nick at ban time, reapply them now.
    private async Task HandleGuildUserAddAsync(GuildUser member)
    {
        if (!IsActive)
            return;

        // A skipped user who is back can hold an overwrite again, so re-check their opt-outs.
        var skippedChannels = _hideSkipStore.RemoveUser(member.Id);
        foreach (
            var honeypot in _moderationConfig.Config.Honeypots.Where(h =>
                skippedChannels.Contains(h.ChannelId)
            )
        )
            await ReconcileHoneypotAsync(honeypot);

        var record = _banRestoreStore.TryTake(member.GuildId, member.Id);
        if (record == null)
            return;

        // Add roles one at a time so a single un-addable role (managed, or above the
        // bot's highest role) doesn't block the rest.
        var restored = 0;
        foreach (var roleId in record.RoleIds)
        {
            try
            {
                await _client.Rest.AddGuildUserRoleAsync(member.GuildId, member.Id, roleId);
                restored++;
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    ex,
                    "Couldn't reapply role {RoleId} to returning user {UserId}.",
                    roleId,
                    member.Id
                );
            }
        }

        if (!string.IsNullOrEmpty(record.Nickname))
        {
            try
            {
                await _client.Rest.ModifyGuildUserAsync(
                    member.GuildId,
                    member.Id,
                    o => o.Nickname = record.Nickname
                );
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    ex,
                    "Couldn't restore nickname for returning user {UserId}.",
                    member.Id
                );
            }
        }

        _logger.Information(
            "Restored {Restored}/{Total} role(s){Nick} for returning user {Username} ({UserId}).",
            restored,
            record.RoleIds.Count,
            string.IsNullOrEmpty(record.Nickname) ? "" : " and nickname",
            member.Username,
            member.Id
        );
    }

    // "*" wildcard => any emoji opts out; otherwise an exact emoji-name match.
    private const string AnyEmojiWildcard = "*";

    private static bool OptOutEmojiMatches(Honeypot honeypot, string? reactionEmojiName) =>
        honeypot.OptOutEmoji == AnyEmojiWildcard
        || string.Equals(honeypot.OptOutEmoji, reactionEmojiName, StringComparison.Ordinal);

    // Does the user still have any qualifying opt-out reaction on the message?
    // Used on remove to avoid unhiding while another reaction remains.
    private async Task<bool> UserHasOptOutReactionAsync(Honeypot honeypot, ulong userId)
    {
        try
        {
            foreach (var emoji in await GetOptOutEmojisAsync(honeypot))
            {
                await foreach (
                    var user in _client.Rest.GetMessageReactionsAsync(
                        honeypot.OptOutMessageChannelId,
                        honeypot.OptOutMessageId,
                        emoji
                    )
                )
                {
                    if (user.Id == userId)
                        return true;
                }
            }
        }
        catch (Exception ex)
        {
            // On error, assume a reaction may remain so we don't wrongly unhide.
            _logger.Error(
                ex,
                "Failed to check remaining reactions for user {UserId} on message {MessageId}; keeping hidden.",
                userId,
                honeypot.OptOutMessageId
            );
            return true;
        }
        return false;
    }

    // The emoji currently on the message that count as opt-out for this honeypot: all of
    // them for "*", otherwise the configured one. Read from the message because fetching a
    // custom emoji's reactions needs its ID, and the config only holds its name.
    private async Task<List<ReactionEmojiProperties>> GetOptOutEmojisAsync(Honeypot honeypot)
    {
        var message = await _client.Rest.GetMessageAsync(
            honeypot.OptOutMessageChannelId,
            honeypot.OptOutMessageId
        );
        return
        [
            .. message
                .Reactions.Where(r =>
                    r.Emoji.Name is not null && OptOutEmojiMatches(honeypot, r.Emoji.Name)
                )
                .Select(r =>
                    r.Emoji.Id is { } id
                        ? new ReactionEmojiProperties(r.Emoji.Name!, id)
                        : new ReactionEmojiProperties(r.Emoji.Name!)
                ),
        ];
    }

    private const int UnknownOverwriteErrorCode = 10009;

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
            _hideSkipStore.Remove(channelId, userId);
            _logger.Information("Hid channel {ChannelId} for user {UserId}.", channelId, userId);
        }
        catch (RestException ex) when (ex.Error?.Code == UnknownOverwriteErrorCode)
        {
            // Reactions outlive membership, and Discord answers this way when the target
            // can't hold an overwrite, which is expected for a user who left the server.
            // Count it as done so reconciles stop retrying them.
            _hideSkipStore.Add(channelId, userId);
            _logger.Warning(
                "Couldn't hide channel {ChannelId} for user {UserId}: Unknown Overwrite (user may have left the server). Not retrying unless they rejoin or react again.",
                channelId,
                userId
            );
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
        catch (RestException ex) when (ex.Error?.Code == UnknownOverwriteErrorCode)
        {
            // Nothing to remove, e.g. the hide was skipped because the user had left.
            _logger.Debug(
                "No overwrite to remove on channel {ChannelId} for user {UserId}.",
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
        foreach (var honeypot in _moderationConfig.Config.Honeypots)
            await ReconcileHoneypotAsync(honeypot);
    }

    // Bring a honeypot channel's per-user "hide" overwrites in line with who
    // currently reacts to its opt-out message: hide everyone reacting, restore
    // everyone no longer reacting. Self-correcting, so it handles startup backlog
    // and bulk mod reaction removals (remove-all / remove-emoji) alike.
    private async Task ReconcileHoneypotAsync(Honeypot honeypot)
    {
        if (!honeypot.HasOptOut || honeypot.ChannelId == 0)
            return;

        try
        {
            var reacted = new HashSet<ulong>();
            foreach (var emoji in await GetOptOutEmojisAsync(honeypot))
            {
                await foreach (
                    var user in _client.Rest.GetMessageReactionsAsync(
                        honeypot.OptOutMessageChannelId,
                        honeypot.OptOutMessageId,
                        emoji
                    )
                )
                {
                    if (!user.IsBot)
                        reacted.Add(user.Id);
                }
            }

            // Existing per-user "hide" overwrites we previously wrote (deny == ViewChannel only).
            var hiddenNow = new HashSet<ulong>();
            if (
                await _client.Rest.GetChannelAsync(honeypot.ChannelId) is IGuildChannel guildChannel
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

            _hideSkipStore.RemoveNotReacting(honeypot.ChannelId, reacted);

            foreach (
                var userId in reacted.Where(u =>
                    !hiddenNow.Contains(u) && !_hideSkipStore.Contains(honeypot.ChannelId, u)
                )
            )
            {
                _logger.Information(
                    "Reconcile: applying hide for reacting user {UserId}.",
                    userId
                );
                await HideChannelForUserAsync(honeypot.ChannelId, userId);
            }

            foreach (var userId in hiddenNow.Where(u => !reacted.Contains(u)))
            {
                _logger.Information(
                    "Reconcile: restoring user {UserId} who no longer reacts.",
                    userId
                );
                await RestoreChannelForUserAsync(honeypot.ChannelId, userId);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(
                ex,
                "Failed to reconcile honeypot opt-out on message {MessageId}.",
                honeypot.OptOutMessageId
            );
        }
    }

    // Bulk reaction removals by a mod (clear-all or clear-one-emoji) don't fire
    // per-user remove events, so reconcile every honeypot tied to that message.
    private async Task ReconcileByOptOutMessageAsync(ulong messageId)
    {
        if (!_isReady)
            return;

        foreach (
            var honeypot in _moderationConfig.Config.Honeypots.Where(h =>
                h.HasOptOut && h.OptOutMessageId == messageId
            )
        )
            await ReconcileHoneypotAsync(honeypot);
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

            _isReady = true;
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

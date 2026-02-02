using System.Text.Json;
using System.Text.Json.Serialization;
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

internal class DiscordPlatform : IBotPlatform
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

    public event CommandEventHandler? OnCommand;

    internal DiscordPlatform(IBot bot)
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
                    | GatewayIntents.MessageContent,
            }
        );

        _commandService = new ApplicationCommandService<SlashCommandContext>();

        _client.InteractionCreate += async interaction => await HandleInteractionAsync(interaction);
        _client.MessageCreate += async message => await HandleMessageAsync(message);
        _client.Ready += async args => await OnClientReady(args);

        LoadCommands();
        StartAsync().GetAwaiter().GetResult();
        _logger.Information($"Started for {Bot.Name}.");
    }

    internal async Task StartAsync()
    {
        if (string.IsNullOrWhiteSpace(_token))
        {
            _logger.Fatal("Missing bot token in DiscordTokens.json file!");
            throw new InvalidDataException("Missing bot token!");
        }
        await _client.StartAsync();
    }

    public async Task Shutdown()
    {
        _logger.Information($"Shutting down for {Bot.Name}...");
        await _client.CloseAsync();
        _logger.Information($"Shutdown for {Bot.Name} complete.");
    }

    private async Task HandleInteractionAsync(Interaction interaction)
    {
        if (interaction is not SlashCommandInteraction slashCommand)
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
                var embed = new EmbedProperties()
                    .WithTitle(matchingCommand.Response.EmbedTitle ?? string.Empty)
                    .WithDescription(matchingCommand.Response.EmbedDescription ?? string.Empty);

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

    private async Task HandleMessageAsync(Message message)
    {
        if (message.Author.IsBot || !message.Content.StartsWith('!'))
            return;

        try
        {
            var commandText = message.Content[1..].Split(' ')[0].ToLowerInvariant();

            var matchingCommand = Commands
                .Where(c => c.CommandType.HasFlag(BotCommandTypes.TextCommand))
                .FirstOrDefault(c =>
                    c.Name.Equals(commandText, StringComparison.InvariantCultureIgnoreCase)
                );

            if (matchingCommand is null)
                return;

            var response = await matchingCommand.Response.PrepareResponse();

            if (!response)
            {
                await message.ReplyAsync("No response from command.");
                return;
            }

            var messageProps = new ReplyMessageProperties();

            if (
                !string.IsNullOrEmpty(matchingCommand.Response.EmbedTitle)
                || !string.IsNullOrEmpty(matchingCommand.Response.EmbedDescription)
            )
            {
                var embed = new EmbedProperties()
                    .WithTitle(matchingCommand.Response.EmbedTitle ?? string.Empty)
                    .WithDescription(matchingCommand.Response.EmbedDescription ?? string.Empty);
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

    private async Task OnClientReady(ReadyEventArgs args)
    {
        try
        {
            var existingCommands = await _client.Rest.GetGlobalApplicationCommandsAsync(
                args.User.Id
            );

            var desiredCommands = new List<ApplicationCommandProperties>();
            foreach (var command in Commands)
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

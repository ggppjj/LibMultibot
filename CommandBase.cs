using System.Drawing;
using LibMultibot.Helper_Classes;
using LibMultibot.Interfaces;
using LibMultibot.Platforms;
using LibMultibot.Users;
using Serilog;

namespace LibMultibot;

public abstract class CommandBase : IBotCommand, IBotResponse
{
    // IBotCommand
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract BotCommandTypes CommandType { get; }
    public abstract List<BotPlatforms> CommandPlatforms { get; }
    public IBot OriginatingBot { get; }
    public IBotResponse Response => this;
    public virtual bool IsInitialized => true;
    public virtual Task<bool> Init() => Task.FromResult(true);
    public bool IsActive { get; set; } = true;
    public CancellationToken CancellationToken { get; set; }
    public virtual bool IsAdminCommand { get; } = false;
    public List<User>? AdminUsers { get; set; }
    public List<ulong>? RestrictedToChannelIDs { get; set; }
    public string? MessageContext { get; set; }
    public ulong? MessageAuthorId { get; set; }

    // IBotResponse
    public IBotCommand OriginatingCommand => this;
    public virtual BotPlatforms ResponsePlatform => CommandPlatforms[0];
    public string? Message { get; set; }
    public string? EmbedFilePath { get; set; }
    public string? EmbedFileName { get; set; }
    public virtual Color? EmbedColor { get; } = null;
    public string? EmbedTitle { get; set; }
    public string? EmbedDescription { get; set; }

    protected readonly ILogger _logger;

    protected CommandBase(IBot originatingBot, CancellationToken cancellationToken = default)
    {
        OriginatingBot = originatingBot;
        CancellationToken = cancellationToken;
        _logger = LogController.BotLogging.ForBotComponent(GetType(), originatingBot);
    }

    public abstract Task<bool> PrepareResponse();
}

using LibMultibot.Platforms;

namespace LibMultibot.Interfaces;

[Flags]
public enum BotCommandTypes
{
    None = 0,
    SlashCommand = 1,
    TextCommand = 2,
}

public interface IBotCommand
{
    string Name { get; }
    string Description { get; }
    BotCommandTypes CommandType { get; }
    IBotResponse Response { get; }
    List<BotPlatforms> CommandPlatforms { get; }
    IBot OriginatingBot { get; }
    Task<bool> Init();
    bool IsActive { get; set; }
    CancellationToken CancellationToken { get; set; }
}

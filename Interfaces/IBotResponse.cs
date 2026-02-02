using LibMultibot.Platforms;

namespace LibMultibot.Interfaces;

public interface IBotResponse
{
    public BotPlatforms ResponsePlatform { get; }
    public string Message { get; }
    public string? EmbedFilePath { get; }
    public string? EmbedFileName { get; }
    public string? EmbedTitle { get; }
    public string? EmbedDescription { get; }
    public IBotCommand OriginatingCommand { get; }
    public Task<bool> PrepareResponse();
}

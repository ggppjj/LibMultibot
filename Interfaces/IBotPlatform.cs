namespace LibMultibot.Interfaces;

public interface IBotPlatform
{
    string Name { get; }
    IBot Bot { get; }
    List<IBotCommand> Commands { get; }
    Task StartAsync();
    Task Shutdown();
    Task SendMessage(string message, ulong? channelId, bool trackedMessage = false);
    Task SendEmbed(
        ulong channelId,
        string? title,
        string? description,
        System.Drawing.Color? color = null,
        string? filePath = null,
        string? fileName = null
    );
    bool IsActive { get; set; }
}

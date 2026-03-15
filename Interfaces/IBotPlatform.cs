namespace LibMultibot.Interfaces;

public interface IBotPlatform
{
    string Name { get; }
    IBot Bot { get; }
    List<IBotCommand> Commands { get; }
    Task Shutdown();
    Task SendMessage(string message, ulong? channelId, bool trackedMessage = false);
    bool IsActive { get; set; }
}

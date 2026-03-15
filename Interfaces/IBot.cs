namespace LibMultibot.Interfaces;

public interface IBot
{
    string Name { get; }
    List<IBotCommand> Commands { get; }
    List<IBotScheduledMessage>? ScheduledMessages { get; }
    void OnCommand(string message);
    Task<bool> Init();
    Task Shutdown();
    Task RequestShutdown();
    Task SendMessage(string message, ulong channelId, bool trackedMessage = false);
    bool IsActive { get; set; }
    CancellationToken CancellationToken { get; set; }
}

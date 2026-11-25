namespace LibMultibot.Interfaces;

public interface IBot
{
    string Name { get; }
    List<IBotCommand> Commands { get; }
    List<IBotScheduledMessage>? ScheduledMessages { get; }
    List<IBotMessageListener> MessageListeners { get; }
    void OnCommand(string message);
    Task<bool> Init();

    /// <summary>
    /// Completes when this bot stops on its own, leaving other bots running. The result is
    /// true if it stopped because of a failure.
    /// </summary>
    Task<bool> Stopped { get; }
    Task Shutdown();

    /// <summary>Stops this bot only.</summary>
    Task RequestBotShutdown();

    /// <summary>Shuts down the whole process, and every bot in it.</summary>
    Task RequestProcessShutdown();
    Task SendMessage(string message, ulong channelId, bool trackedMessage = false);
    Task SendEmbed(
        ulong channelId,
        string? title,
        string? description,
        System.Drawing.Color? color = null,
        string? filePath = null,
        string? fileName = null
    );
    bool IsActive { get; set; }
    CancellationToken CancellationToken { get; set; }
}

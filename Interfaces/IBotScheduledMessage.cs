namespace LibMultibot.Interfaces;

public interface IBotScheduledMessage
{
    string Name { get; }
    string Description { get; }
    string Message { get; set; }
    int FrequencyMinutes { get; set; }
    bool IsEnabled { get; set; }
    event Action<object> OnReply;
    List<ulong> ChannelIds { get; }
    IBotCommand ManagementCommand { get; }
    void Start();
    void Stop();
    void TriggerNow();
}

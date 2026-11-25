namespace LibMultibot.Interfaces;

public interface IBotPlatform
{
    string Name { get; }
    IBot Bot { get; }
    List<IBotCommand> Commands { get; }
    Task StartAsync();
    Task Shutdown();
    Task SendMessage(string message, ulong? channelId, bool trackedMessage = false);

    /// <summary>Returns false if the user could not be messaged, e.g. they block DMs.</summary>
    Task<bool> SendDirectMessage(ulong userId, string message);
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

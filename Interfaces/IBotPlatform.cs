namespace LibMultibot.Interfaces;

public interface IBotPlatform
{
    string Name { get; }
    IBot Bot { get; }
    List<IBotCommand> Commands { get; }
    Task Shutdown();
    bool IsActive { get; set; }
}

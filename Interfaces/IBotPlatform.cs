namespace LibMultibot.Interfaces;

internal interface IBotPlatform
{
    string Name { get; }
    IBot Bot { get; }
    List<IBotCommand> Commands { get; }
    Task Shutdown();
}

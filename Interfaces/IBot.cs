namespace LibMultibot.Interfaces;

public interface IBot
{
    string Name { get; }
    List<IBotCommand> Commands { get; }
    void OnCommand(string message);
    Task<bool> Init();
    Task Shutdown();
}

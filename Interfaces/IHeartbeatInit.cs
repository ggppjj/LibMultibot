namespace LibMultibot.Interfaces;

public interface IHeartbeatInit
{
    IProgress<string>? InitProgress { get; set; }
}

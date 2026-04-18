using LibMultibot.Helper_Classes;
using LibMultibot.Interfaces;
using Serilog;

namespace LibMultibot;

public abstract class BotBase : IBot
{
    public abstract string Name { get; }
    public List<IBotCommand> Commands { get; } = [];
    public List<IBotScheduledMessage>? ScheduledMessages { get; } = [];
    public bool IsActive { get; set; } = true;
    public CancellationToken CancellationToken { get; set; }

    private readonly List<IBotPlatform> _platforms = [];
    protected readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdownSource;

    private const int CommandInactivityTimeoutMs = 30_000;
    private const int HeartbeatLogIntervalMs = 10_000;

    protected BotBase(CancellationTokenSource shutdownSource)
    {
        _shutdownSource = shutdownSource;
        CancellationToken = shutdownSource.Token;
        _logger = LogController.SetupLogging(GetType());
    }

    protected abstract IEnumerable<IBotPlatform> CreatePlatforms();

    public async Task<bool> Init()
    {
        _logger.Information("Starting...");
        StartCommandInits();
        try
        {
            foreach (var platform in CreatePlatforms())
            {
                await platform.StartAsync();
                _platforms.Add(platform);
            }
            _logger.Information("Started.");
            return true;
        }
        catch (InvalidDataException e)
        {
            _logger.Fatal(e.Message);
            throw;
        }
    }

    private void StartCommandInits()
    {
        var commandInitTasks = Commands.Select(cmd => InitCommandInBackground(cmd)).ToList();
        _ = MonitorCommandInitsAsync(commandInitTasks);
    }

    private async Task MonitorCommandInitsAsync(List<Task<bool>> commandInitTasks)
    {
        var results = await Task.WhenAll(commandInitTasks);
        if (commandInitTasks.Count > 0 && results.All(r => !r))
        {
            _logger.Fatal("All commands failed to initialize. Requesting shutdown.");
            await RequestShutdown();
        }
    }

    private async Task<bool> InitCommandInBackground(IBotCommand cmd)
    {
        var originalToken = cmd.CancellationToken;
        using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(originalToken);
        watchdogCts.CancelAfter(CommandInactivityTimeoutMs);
        cmd.CancellationToken = watchdogCts.Token;

        watchdogCts.Token.Register(() =>
        {
            if (!originalToken.IsCancellationRequested)
                _logger.Warning(
                    $"'{cmd.Name}' init watchdog fired — no heartbeat within {CommandInactivityTimeoutMs / 1000}s."
                );
        });

        if (cmd is IHeartbeatInit heartbeatCmd)
        {
            var lastLogMs = long.MinValue;
            heartbeatCmd.InitProgress = new Progress<string>(msg =>
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var prev = Interlocked.Read(ref lastLogMs);
                if (
                    now - prev >= HeartbeatLogIntervalMs
                    && Interlocked.CompareExchange(ref lastLogMs, now, prev) == prev
                )
                    _logger.Information($"'{cmd.Name}' init heartbeat: {msg}");
                try
                {
                    watchdogCts.CancelAfter(CommandInactivityTimeoutMs);
                }
                catch (ObjectDisposedException) { }
            });
        }

        try
        {
            var result = await cmd.Init();
            if (!result && !watchdogCts.IsCancellationRequested)
                _logger.Warning($"Command '{cmd.Name}' failed to initialize.");
            return result;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Command '{cmd.Name}' threw an exception during initialization.");
            return false;
        }
        finally
        {
            cmd.CancellationToken = originalToken;
            if (cmd is IHeartbeatInit h)
                h.InitProgress = null;
        }
    }

    public virtual void OnCommand(string message) =>
        _logger.Debug("Command received: {Message}", message);

    public virtual async Task SendMessage(
        string message,
        ulong channelId,
        bool trackedMessage = false
    )
    {
        foreach (var platform in _platforms)
            await platform.SendMessage(message, channelId, trackedMessage);
    }

    public Task RequestShutdown()
    {
        _shutdownSource.Cancel();
        return Task.CompletedTask;
    }

    public virtual async Task Shutdown()
    {
        _logger.Information("Shutting down...");
        await Task.WhenAll(_platforms.Select(p => p.Shutdown()));
        _logger.Information("Shutdown complete.");
    }
}

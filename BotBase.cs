using LibMultibot.Helper_Classes;
using LibMultibot.Interfaces;
using Serilog;

namespace LibMultibot;

public abstract class BotBase : IBot
{
    public abstract string Name { get; }
    public List<IBotCommand> Commands { get; } = [];
    public List<IBotScheduledMessage>? ScheduledMessages { get; } = [];
    public List<IBotMessageListener> MessageListeners { get; } = [];
    public bool IsActive { get; set; } = true;
    public CancellationToken CancellationToken { get; set; }
    public Task<bool> Stopped => _stopped.Task;

    private readonly List<IBotPlatform> _platforms = [];

    // Guards _platforms and _hasStopped: the bot can be stopped while Init is still
    // starting platforms.
    private readonly Lock _platformLock = new();
    private bool _hasStopped;
    private readonly TaskCompletionSource<bool> _stopped = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    // Cancelled when this bot stops or the process shuts down, whichever is first.
    private readonly CancellationTokenSource _botCts;
    protected readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdownSource;

    private const int CommandInactivityTimeoutMs = 30_000;
    private const int HeartbeatLogIntervalMs = 10_000;
    private const int HeartbeatMinIntervalMs = 1_000;

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    protected BotBase(CancellationTokenSource shutdownSource)
    {
        _shutdownSource = shutdownSource;
        _botCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownSource.Token);
        CancellationToken = _botCts.Token;
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
                bool failed;
                lock (_platformLock)
                {
                    failed = _hasStopped;
                    if (!failed)
                        _platforms.Add(platform);
                }
                if (failed)
                {
                    await platform.Shutdown();
                    return false;
                }
            }
            _logger.Information("Started.");
            try
            {
                await OnStarted();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error in the bot's startup step.");
            }
            return true;
        }
        catch (InvalidDataException e)
        {
            _logger.Fatal(e.Message);
            throw;
        }
    }

    /// <summary>Runs once every platform has started. A failure here does not stop the bot.</summary>
    protected virtual Task OnStarted() => Task.CompletedTask;

    private List<IBotPlatform> PlatformsSnapshot()
    {
        lock (_platformLock)
            return [.. _platforms];
    }

    // Stops this bot only. Other bots in the process keep running.
    private async Task StopAsync(bool failed)
    {
        List<IBotPlatform> toStop;
        lock (_platformLock)
        {
            if (_hasStopped)
                return;
            _hasStopped = true;
            toStop = [.. _platforms];
            _platforms.Clear();
        }
        IsActive = false;
        // Ends this bot's background work, such as command preloading.
        _botCts.Cancel();
        foreach (var platform in toStop)
        {
            try
            {
                await platform.Shutdown();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error shutting down platform '{platform.Name}'.");
            }
        }
        _stopped.TrySetResult(failed);
        _logger.Information("Stopped.");
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
            _logger.Fatal("All commands failed to initialize. Stopping this bot.");
            await StopAsync(failed: true);
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
                    $"'{cmd.Name}' init watchdog fired: no heartbeat within {CommandInactivityTimeoutMs / 1000}s."
                );
        });

        if (cmd is IHeartbeatInit heartbeatCmd)
        {
            var lastLogMs = 0L;
            var lastHeartbeatTick = Environment.TickCount64;
            // Not Progress<T>: it queues every report on the thread pool, and a command can
            // report millions of times during init, which delays everything else in the bot.
            heartbeatCmd.InitProgress = new InlineProgress(msg =>
            {
                var tick = Environment.TickCount64;
                if (tick - Volatile.Read(ref lastHeartbeatTick) < HeartbeatMinIntervalMs)
                    return;
                Volatile.Write(ref lastHeartbeatTick, tick);

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
        foreach (var platform in PlatformsSnapshot())
            await platform.SendMessage(message, channelId, trackedMessage);
    }

    public virtual async Task SendEmbed(
        ulong channelId,
        string? title,
        string? description,
        System.Drawing.Color? color = null,
        string? filePath = null,
        string? fileName = null
    )
    {
        // Platforms join this list once started. Fail rather than silently send nowhere, so
        // a caller doesn't count a post made during startup as delivered.
        var platforms = PlatformsSnapshot();
        if (platforms.Count == 0)
            throw new InvalidOperationException("No platforms are running.");

        foreach (var platform in platforms)
            await platform.SendEmbed(channelId, title, description, color, filePath, fileName);
    }

    public Task RequestBotShutdown()
    {
        _logger.Warning("Shutdown of this bot requested.");
        return StopAsync(failed: false);
    }

    public Task RequestProcessShutdown()
    {
        _logger.Warning("Shutdown of the whole process requested.");
        _shutdownSource.Cancel();
        return Task.CompletedTask;
    }

    public virtual async Task Shutdown()
    {
        _logger.Information("Shutting down...");
        await Task.WhenAll(PlatformsSnapshot().Select(p => p.Shutdown()));
        _logger.Information("Shutdown complete.");
    }
}

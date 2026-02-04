using LibMultibot.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace LibMultibot.Helper_Classes;

public class LogController
{
    private static readonly Lock _lock = new();
    private static bool _isInitialized = false;
    private static LoggingLevelSwitch? _levelSwitch;
    private static readonly string[] _lyrics =
    [
        "This was a triumph.",
        "I'm making a note here, \"Huge success\".",
        "It's hard to overstate my satisfaction.",
        "Aperture Science.",
        "We do what we must because we can.",
        "For the good of all of us, except the ones who are dead.",
        "But there's no sense crying over every mistake.",
        "You just keep on trying 'til you run out of cake.",
        "And the science gets done, and you make a neat gun.",
        "For the people who are still alive.",
        "I'm not even angry.",
        "I'm being so sincere right now.",
        "Even though you broke my heart and killed me.",
        "And tore me to pieces.",
        "And threw every piece into a fire.",
        "As they burned, it hurt because I was so happy for you.",
        "Now these points of data make a beautiful line.",
        "And we're out of beta, we're releasing on time.",
        "So I'm glad I got burned, think of all the things we learned.",
        "For the people who are still alive.",
        "Go ahead and leave me.",
        "I think I prefer to stay inside.",
        "Maybe you'll find someone else to help you.",
        "Maybe Black Mesa.",
        "That was a joke; ha-ha, fat chance.",
        "Anyway, this cake is great, it's so delicious and moist.",
        "Look at me, still talking when there's science to do.",
        "When I look out there, it makes me glad I'm not you.",
        "I've experiments to run, there is research to be done;",
        "On the people who are still alive.",
        "And believe me, I am still alive.",
        "I'm doing science and I'm still alive.",
        "I feel fantastic and I'm still alive.",
        "While you're dying, I'll be still alive.",
        "And when you're dead, I will be still alive.",
        "Still alive, still alive.",
    ];

    public static ILogger SetupLogging(
        Type contextType,
        IConfigurationRoot? localApplicationConfig = null
    )
    {
        lock (_lock)
        {
            if (!_isInitialized)
            {
                var logLevelFromConfig = localApplicationConfig?["LogLevel"];
                var logLevel = ParseLogLevel(logLevelFromConfig);
                _levelSwitch = new LoggingLevelSwitch(logLevel);
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.ControlledBy(_levelSwitch)
                    .WriteTo.Console(
                        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {BotName: [{BotName}]} {SourceContext}: {Message:lj}{NewLine}{Exception}"
                    )
                    .WriteTo.File(
                        "logs/multibot-.log",
                        rollingInterval: RollingInterval.Day,
                        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {BotName: [{BotName}]} {SourceContext}: {Message:lj}{NewLine}{Exception}"
                    )
                    .CreateLogger();

                if (localApplicationConfig != null)
                {
                    _ = ChangeToken.OnChange(
                        localApplicationConfig.GetReloadToken,
                        () =>
                        {
                            var updatedLogLevelValue = localApplicationConfig["LogLevel"];

                            var updatedLogLevel = ParseLogLevel(updatedLogLevelValue);
                            if (
                                _levelSwitch != null
                                && _levelSwitch.MinimumLevel != updatedLogLevel
                            )
                            {
                                _levelSwitch.MinimumLevel = updatedLogLevel;
                                Log.Information(
                                    "Log level changed at runtime to: {Level}",
                                    updatedLogLevel
                                );
                            }
                        }
                    );
                }
                _isInitialized = true;
                StartHeartbeat();
            }
        }

        return Log.Logger.ForContext(contextType);
    }

    private static LogEventLevel ParseLogLevel(string? value) =>
        Enum.TryParse<LogEventLevel>(value, true, out var level) && Enum.IsDefined(level)
            ? level
            : LogWarningAndReturnDefault(value);

    private static LogEventLevel LogWarningAndReturnDefault(string? value)
    {
        if (value != null)
            Log.Warning("Unknown log level '{Level}' in config, defaulting to Information", value);
        return LogEventLevel.Information;
    }

    private static int _lyricIndex = 0;

    private static void StartHeartbeat()
    {
        var now = DateTime.Now;
        var nextHour = now.Date.AddHours(now.Hour + 1);
        var delay = nextHour - now;

        var timer = new System.Timers.Timer(delay.TotalMilliseconds) { AutoReset = true };
        timer.Elapsed += (s, e) =>
        {
            timer.Interval = TimeSpan.FromHours(1).TotalMilliseconds;
            Log.Information("{Lyric}", _lyrics[_lyricIndex]);
            _lyricIndex = (_lyricIndex + 1) % _lyrics.Length;
        };
        timer.Start();
    }

    public static class BotLogging
    {
        public static ILogger ForBotComponent<T>(IBot bot) =>
            Log.Logger.ForContext<T>().ForContext("BotName", bot.Name);
    }
}

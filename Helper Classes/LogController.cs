using LibMultibot.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

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

    private const string BloodRed = "\x1b[38;2;139;0;0m";
    private const string CoolBlue = "\x1b[38;2;80;200;255m";

    // The Code console theme's colours for the timestamp and its brackets.
    private const string BracketGray = "\x1b[38;5;0242m";
    private const string TimeGray = "\x1b[38;5;0246m";

    private const string Reset = "\x1b[0m";

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

                // Plain output templates, not Serilog.Expressions: that package compiles its
                // templates at runtime, which is not AOT or trim safe. Conditional parts come
                // from LogLineEnricher, and the heartbeat's colours from its own console logger.
                const string consoleTemplate =
                    "[{Timestamp:HH:mm:ss} {Level:u3}]{BotPrefix:l} "
                    + "{SourceContext:l}: {Message:l}{NewLine}{Exception}";
                // All colours are written out here because a theme resets the colour before
                // each value, which would remove the heartbeat's blue.
                const string heartbeatRedirectedTemplate =
                    "[{Timestamp:HH:mm:ss} HRT]{BotPrefix:l} "
                    + "{SourceContext:l}: {Message:l}{NewLine}{Exception}";
                const string heartbeatConsoleTemplate =
                    BracketGray
                    + "["
                    + Reset
                    + TimeGray
                    + "{Timestamp:HH:mm:ss}"
                    + Reset
                    + " "
                    + BloodRed
                    + "HRT"
                    + Reset
                    + BracketGray
                    + "]"
                    + Reset
                    + "{BotPrefix:l} "
                    + CoolBlue
                    + "{SourceContext:l}: {Message:l}"
                    + Reset
                    + "{NewLine}{Exception}";
                const string fileTemplate =
                    "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {LevelTag:l}]{BotPrefix:l} "
                    + "{SourceContext:l}: {Message:l}{NewLine}{Exception}";

                // The enricher goes on each sub-logger because a root enricher runs before
                // the properties added by ForContext, and would not see them.
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.ControlledBy(_levelSwitch)
                    .WriteTo.Logger(console =>
                        console
                            .Enrich.With<LogLineEnricher>()
                            .Filter.ByExcluding(IsHeartbeat)
                            .WriteTo.Console(
                                outputTemplate: consoleTemplate,
                                theme: AnsiConsoleTheme.Code
                            )
                    )
                    .WriteTo.Logger(heartbeat =>
                        heartbeat
                            .Enrich.With<LogLineEnricher>()
                            .Filter.ByIncludingOnly(IsHeartbeat)
                            .WriteTo.Console(
                                // Written-out colours would otherwise end up in piped output.
                                outputTemplate: Console.IsOutputRedirected
                                    ? heartbeatRedirectedTemplate
                                    : heartbeatConsoleTemplate,
                                theme: ConsoleTheme.None
                            )
                    )
                    .WriteTo.Logger(file =>
                        file.Enrich.With<LogLineEnricher>()
                            .WriteTo.File(
                                "logs/multibot-.log",
                                outputTemplate: fileTemplate,
                                rollingInterval: RollingInterval.Day
                            )
                    )
                    .CreateLogger();

                // Parsed again because the first parse ran before any logger existed, which
                // dropped its warning about an unknown level.
                _ = ParseLogLevel(logLevelFromConfig);

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

    private static bool IsHeartbeat(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue("SourceContext", out var context)
        && context is ScalarValue { Value: "HEARTBEAT" };

    private sealed class LogLineEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            // An output template prints a missing property's name, so this can't be left out.
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("SourceContext", ""));

            var botPrefix =
                logEvent.Properties.TryGetValue("BotName", out var botName)
                && botName is ScalarValue { Value: string name }
                    ? $" {name} -"
                    : "";
            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty("BotPrefix", botPrefix));

            var levelTag = IsHeartbeat(logEvent)
                ? "HRT"
                : logEvent.Level switch
                {
                    LogEventLevel.Verbose => "VRB",
                    LogEventLevel.Debug => "DBG",
                    LogEventLevel.Information => "INF",
                    LogEventLevel.Warning => "WRN",
                    LogEventLevel.Error => "ERR",
                    _ => "FTL",
                };
            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty("LevelTag", levelTag));
        }
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
        var heartbeatLogger = Log.Logger.ForContext("SourceContext", "HEARTBEAT");

        // Recalculated on each tick: a fixed one-hour interval would drift off the hour.
        static double MsUntilNextHour()
        {
            var now = DateTime.Now;
            // A timer firing just before the hour must not count that same hour as next.
            var soon = now.AddSeconds(1);
            return (soon.Date.AddHours(soon.Hour + 1) - now).TotalMilliseconds;
        }

        var timer = new System.Timers.Timer(MsUntilNextHour()) { AutoReset = true };
        timer.Elapsed += (s, e) =>
        {
            timer.Interval = MsUntilNextHour();
            heartbeatLogger.Information("{Lyric}", _lyrics[_lyricIndex]);
            _lyricIndex = (_lyricIndex + 1) % _lyrics.Length;
        };
        timer.Start();
    }

    public static class BotLogging
    {
        public static ILogger ForBotComponent<T>(IBot bot) =>
            Log.Logger.ForContext<T>().ForContext("BotName", bot.Name);

        public static ILogger ForBotComponent(Type type, IBot bot) =>
            Log.Logger.ForContext(type).ForContext("BotName", bot.Name);
    }
}

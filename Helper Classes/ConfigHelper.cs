using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Serilog;

namespace LibMultibot.Helper_Classes;

public static class ConfigHelper
{
    private static readonly string ConfigDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "Config"
    );

    public static string ProgramConfigPath => Path.Combine(ConfigDirectory, "config.json");

    public static string GetBotConfigDirectory(string botName) =>
        Path.Combine(ConfigDirectory, botName);

    public static string GetCommandConfigPath(string botName, string commandName) =>
        Path.Combine(GetBotConfigDirectory(botName), $"{commandName}.json");

    public static void EnsureConfigDirectoriesExist()
    {
        if (!Directory.Exists(ConfigDirectory))
        {
            Directory.CreateDirectory(ConfigDirectory);
            Log.Information($"Created config directory: {ConfigDirectory}.");
        }
    }

    public static void EnsureBotConfigDirectoryExists(string botName)
    {
        var botConfigDir = GetBotConfigDirectory(botName);
        if (!Directory.Exists(botConfigDir))
        {
            Directory.CreateDirectory(botConfigDir);
            Log.Information($"Created bot config directory: {botConfigDir}.");
        }
    }
}

public abstract class CommandConfigBase<TConfig>
    where TConfig : class, new()
{
    private readonly string _configFilePath;
    private readonly ILogger _logger;
    private FileSystemWatcher? _configWatcher;
    private CancellationTokenSource? _reloadCts;
    private readonly object _reloadLock = new();

    // The file content this object last wrote or reloaded. The watcher also sees this
    // object's own saves, and those need no reload: the config in memory is already current.
    private volatile string? _lastKnownJson;
    public TConfig Config { get; private set; }

    protected abstract JsonSerializerContext JsonContext { get; }
    protected abstract JsonTypeInfo<TConfig> JsonTypeInfo { get; }
    protected abstract TConfig CreateDefaultConfig();

    protected CommandConfigBase(string botName, string commandName, ILogger logger)
    {
        _logger = logger;
        ConfigHelper.EnsureConfigDirectoriesExist();
        ConfigHelper.EnsureBotConfigDirectoryExists(botName);

        _configFilePath = ConfigHelper.GetCommandConfigPath(botName, commandName);
        Config = new TConfig();

        LoadConfiguration();
        SetupConfigWatcher();
    }

    // Writes the current config to disk. Goes through a temp file so a crash mid-write
    // can't leave a corrupt config, which would be replaced with defaults on next load.
    public void Save()
    {
        var tempPath = _configFilePath + ".tmp";
        var json = JsonSerializer.Serialize(Config, JsonTypeInfo);
        File.WriteAllText(tempPath, json);
        _lastKnownJson = json;
        File.Move(tempPath, _configFilePath, overwrite: true);
    }

    private bool FileMatchesLastKnown()
    {
        try
        {
            return File.ReadAllText(_configFilePath) == _lastKnownJson;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Let the reload deal with it: it retries and reports failures.
            return false;
        }
    }

    // Loading replaces a bad config with defaults, so keep a copy: one typo must not lose
    // the whole file.
    private void PreserveBadConfigFile()
    {
        try
        {
            if (!File.Exists(_configFilePath))
                return;
            var backupPath = $"{_configFilePath}.bad-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(_configFilePath, backupPath, overwrite: true);
            _logger.Warning($"Kept the unreadable config file as {backupPath}.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Could not keep a copy of the unreadable config file.");
        }
    }

    private void LoadConfiguration()
    {
        try
        {
            if (!File.Exists(_configFilePath))
            {
                _logger.Warning($"Config file not found at {_configFilePath}, creating default...");
                CreateAndSaveDefaultConfig();
                return;
            }

            var jsonContent = File.ReadAllText(_configFilePath);
            var config = JsonSerializer.Deserialize(jsonContent, JsonTypeInfo);

            if (config != null)
            {
                Config = config;
                OnConfigLoaded();
                _logger.Information($"Loaded configuration from {_configFilePath}.");
            }
            else
            {
                _logger.Warning("Config file is empty or invalid, using defaults!");
                PreserveBadConfigFile();
                CreateAndSaveDefaultConfig();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error loading configuration, using defaults!");
            PreserveBadConfigFile();
            CreateAndSaveDefaultConfig();
        }
    }

    private void ReloadConfiguration()
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (!File.Exists(_configFilePath))
                {
                    _logger.Warning($"Config file not found at {_configFilePath}, keeping existing config.");
                    return;
                }

                var jsonContent = File.ReadAllText(_configFilePath);
                var config = JsonSerializer.Deserialize(jsonContent, JsonTypeInfo);

                if (config != null)
                {
                    Config = config;
                    _lastKnownJson = jsonContent;
                    OnConfigLoaded();
                    _logger.Information($"Reloaded configuration from {_configFilePath}.");
                }
                else
                {
                    _logger.Warning("Config file is empty or invalid after reload, keeping existing config.");
                }
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && (ex is IOException || ex is JsonException))
            {
                // Writer may still hold the file or the write may be mid-flight; back off and retry.
                _logger.Debug($"Config reload attempt {attempt} failed ({ex.GetType().Name}), retrying...");
                Thread.Sleep(250 * attempt);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error reloading configuration, keeping existing config.");
                return;
            }
        }
    }

    private void CreateAndSaveDefaultConfig()
    {
        try
        {
            Config = CreateDefaultConfig();
            var json = JsonSerializer.Serialize(Config, JsonTypeInfo);
            File.WriteAllText(_configFilePath, json);
            OnConfigLoaded();
            _logger.Information($"Created default config at {_configFilePath}.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to create default configuration!");
            Config = new TConfig();
        }
    }

    private void SetupConfigWatcher()
    {
        try
        {
            var directory = Path.GetDirectoryName(_configFilePath);
            if (string.IsNullOrEmpty(directory))
                return;

            // Watch the whole directory (no name filter) because many editors save atomically:
            // write a temp file, then rename it over the target. That surfaces as
            // Renamed/Created/Deleted rather than Changed, and a name filter can miss the
            // temp-file rename. We filter by final file name ourselves.
            var targetName = Path.GetFileName(_configFilePath);
            _configWatcher = new FileSystemWatcher(directory)
            {
                Filter = "*",
                NotifyFilter =
                    NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.FileName
                    | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };

            bool IsTarget(string? name) =>
                string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase);

            void ScheduleReload(string reason)
            {
                CancellationToken token;
                lock (_reloadLock)
                {
                    _reloadCts?.Cancel();
                    _reloadCts = new CancellationTokenSource();
                    token = _reloadCts.Token;
                }

                Task.Delay(500, token).ContinueWith(t =>
                {
                    if (t.IsCanceled || FileMatchesLastKnown())
                        return;
                    _logger.Information($"Config file {reason}, reloading...");
                    ReloadConfiguration();
                }, CancellationToken.None);
            }

            _configWatcher.Changed += (_, e) =>
            {
                if (IsTarget(e.Name))
                    ScheduleReload("changed");
            };
            _configWatcher.Created += (_, e) =>
            {
                if (IsTarget(e.Name))
                    ScheduleReload("created");
            };
            _configWatcher.Renamed += (_, e) =>
            {
                if (IsTarget(e.Name) || IsTarget(e.OldName))
                    ScheduleReload("renamed");
            };
            _configWatcher.Error += (_, e) =>
                _logger.Warning(e.GetException(), "Config file watcher error!");

            _logger.Information("Config file watcher enabled.");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to setup config file watcher!");
        }
    }

    protected virtual void OnConfigLoaded() { }
}

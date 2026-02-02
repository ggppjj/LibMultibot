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
                CreateAndSaveDefaultConfig();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error loading configuration, using defaults!");
            CreateAndSaveDefaultConfig();
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

            _configWatcher = new FileSystemWatcher(directory)
            {
                Filter = Path.GetFileName(_configFilePath),
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            _configWatcher.Changed += (sender, e) =>
            {
                Task.Delay(500)
                    .ContinueWith(_ =>
                    {
                        _logger.Information("Config file changed, reloading...");
                        LoadConfiguration();
                    });
            };

            _logger.Information("Config file watcher enabled.");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to setup config file watcher!");
        }
    }

    protected virtual void OnConfigLoaded() { }
}

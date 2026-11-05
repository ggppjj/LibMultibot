using System.Text.Json;
using System.Text.Json.Serialization;
using LibMultibot.Helper_Classes;
using Serilog;

namespace LibMultibot.Platforms;

public class HideSkipRecord
{
    public ulong ChannelId { get; set; }
    public ulong UserId { get; set; }
}

public class HideSkipData
{
    public List<HideSkipRecord> Records { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(HideSkipData))]
internal partial class HideSkipJsonContext : JsonSerializerContext { }

/// <summary>
/// Users whose honeypot hide failed with Unknown Overwrite. Their reaction outlives their
/// membership, so without this every reconcile would retry them forever. Bot-managed state
/// (not user-edited), so it lives next to the configs but has no file watcher.
/// </summary>
internal class HideSkipStore
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private HideSkipData _data = new();

    public HideSkipStore(string botName, ILogger logger)
    {
        _logger = logger;
        ConfigHelper.EnsureConfigDirectoriesExist();
        ConfigHelper.EnsureBotConfigDirectoryExists(botName);
        _path = ConfigHelper.GetCommandConfigPath(botName, "HideSkips");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;
            var json = File.ReadAllText(_path);
            var data = JsonSerializer.Deserialize(json, HideSkipJsonContext.Default.HideSkipData);
            if (data != null)
                _data = data;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load hide-skip store; starting empty.");
        }
    }

    private void Persist()
    {
        try
        {
            var json = JsonSerializer.Serialize(_data, HideSkipJsonContext.Default.HideSkipData);
            // Through a temp file, so a crash mid-write can't corrupt the store.
            var tempPath = _path + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save hide-skip store.");
        }
    }

    public bool Contains(ulong channelId, ulong userId)
    {
        lock (_lock)
            return _data.Records.Any(r => r.ChannelId == channelId && r.UserId == userId);
    }

    public void Add(ulong channelId, ulong userId)
    {
        lock (_lock)
        {
            if (_data.Records.Any(r => r.ChannelId == channelId && r.UserId == userId))
                return;
            _data.Records.Add(new HideSkipRecord { ChannelId = channelId, UserId = userId });
            Persist();
        }
    }

    public void Remove(ulong channelId, ulong userId)
    {
        lock (_lock)
        {
            if (_data.Records.RemoveAll(r => r.ChannelId == channelId && r.UserId == userId) > 0)
                Persist();
        }
    }

    /// <summary>Forget a user everywhere; returns the channels they were skipped on.</summary>
    public List<ulong> RemoveUser(ulong userId)
    {
        lock (_lock)
        {
            var channels = _data
                .Records.Where(r => r.UserId == userId)
                .Select(r => r.ChannelId)
                .ToList();
            if (channels.Count > 0)
            {
                _data.Records.RemoveAll(r => r.UserId == userId);
                Persist();
            }
            return channels;
        }
    }

    /// <summary>Forget a channel's users who are no longer reacting.</summary>
    public void RemoveNotReacting(ulong channelId, IReadOnlySet<ulong> reacting)
    {
        lock (_lock)
        {
            var removed = _data.Records.RemoveAll(r =>
                r.ChannelId == channelId && !reacting.Contains(r.UserId)
            );
            if (removed > 0)
                Persist();
        }
    }
}

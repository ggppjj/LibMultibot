using System.Text.Json;
using System.Text.Json.Serialization;
using LibMultibot.Helper_Classes;
using Serilog;

namespace LibMultibot.Platforms;

/// <summary>Snapshot of a user's roles + nickname captured when the bot banned them.</summary>
public class BanRestoreRecord
{
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }

    /// <summary>Role IDs the user held at ban time (excludes the @everyone role).</summary>
    public List<ulong> RoleIds { get; set; } = [];

    /// <summary>Server nickname at ban time, if any.</summary>
    public string? Nickname { get; set; }

    public DateTimeOffset BannedAt { get; set; }
}

public class BanRestoreData
{
    public List<BanRestoreRecord> Records { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(BanRestoreData))]
internal partial class BanRestoreJsonContext : JsonSerializerContext { }

/// <summary>
/// Persists role/nick snapshots of users the bot bans so they can be reapplied
/// if the user is later unbanned and rejoins. Bot-managed state (not user-edited),
/// so it lives next to the configs but has no file watcher.
/// </summary>
internal class BanRestoreStore
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private BanRestoreData _data = new();

    public BanRestoreStore(string botName, ILogger logger)
    {
        _logger = logger;
        ConfigHelper.EnsureConfigDirectoriesExist();
        ConfigHelper.EnsureBotConfigDirectoryExists(botName);
        _path = ConfigHelper.GetCommandConfigPath(botName, "BanRestores");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                Persist();
                return;
            }
            var json = File.ReadAllText(_path);
            var data = JsonSerializer.Deserialize(json, BanRestoreJsonContext.Default.BanRestoreData);
            if (data != null)
                _data = data;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load ban-restore store; starting empty.");
        }
    }

    private void Persist()
    {
        try
        {
            var json = JsonSerializer.Serialize(_data, BanRestoreJsonContext.Default.BanRestoreData);
            File.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save ban-restore store.");
        }
    }

    /// <summary>Store (or replace) a snapshot for a banned user.</summary>
    public void Add(BanRestoreRecord record)
    {
        lock (_lock)
        {
            _data.Records.RemoveAll(r => r.GuildId == record.GuildId && r.UserId == record.UserId);
            _data.Records.Add(record);
            Persist();
        }
    }

    /// <summary>Fetch and remove a user's snapshot, if one exists.</summary>
    public BanRestoreRecord? TryTake(ulong guildId, ulong userId)
    {
        lock (_lock)
        {
            var rec = _data.Records.FirstOrDefault(r =>
                r.GuildId == guildId && r.UserId == userId
            );
            if (rec != null)
            {
                _data.Records.Remove(rec);
                Persist();
            }
            return rec;
        }
    }
}

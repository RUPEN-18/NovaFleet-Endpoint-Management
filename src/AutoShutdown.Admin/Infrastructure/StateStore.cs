using System.IO;
using AutoShutdown.Shared;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoShutdown.Admin.Infrastructure;

public sealed class StateStore
{
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string RootPath { get; }
    public string MediaPath { get; }
    private string SettingsPath => Path.Combine(RootPath, "settings.json");
    private string GroupsPath => Path.Combine(RootPath, "groups.json");
    private string SchedulesPath => Path.Combine(RootPath, "schedules.json");
    private string LogsPath => Path.Combine(RootPath, "logs.jsonl");
    private string MediaIndexPath => Path.Combine(RootPath, "media-index.json");
    private string ClientsPath => Path.Combine(RootPath, "clients.json");

    public StateStore()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        RootPath = Path.Combine(programData, "AutoShutdown.Nova", "SystemA");
        MediaPath = Path.Combine(RootPath, "media");
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(MediaPath);
        if (OperatingSystem.IsWindows()) ProtectedAdminFiles.ProtectTree(RootPath);
    }

    public async Task<GlobalSettings> GetSettingsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(SettingsPath))
            {
                var defaults = new GlobalSettings();
                await WriteJsonAtomicAsync(SettingsPath, defaults);
                return defaults;
            }

            return await ReadJsonAsync<GlobalSettings>(SettingsPath) ?? new GlobalSettings();
        }
        finally { _gate.Release(); }
    }

    public async Task SetSettingsAsync(GlobalSettings settings)
    {
        await _gate.WaitAsync();
        try { await WriteJsonAtomicAsync(SettingsPath, settings); }
        finally { _gate.Release(); }
    }

    public async Task<List<GroupDefinition>> GetGroupsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(GroupsPath))
            {
                var empty = new List<GroupDefinition>();
                await WriteJsonAtomicAsync(GroupsPath, empty);
                return empty;
            }
            return await ReadJsonAsync<List<GroupDefinition>>(GroupsPath) ?? new();
        }
        finally { _gate.Release(); }
    }

    public async Task SetGroupsAsync(List<GroupDefinition> groups)
    {
        var clean = groups
            .Where(g => !string.IsNullOrWhiteSpace(g.Name))
            .GroupBy(g => g.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new GroupDefinition
            {
                Name = g.Key,
                Members = g.SelectMany(x => x.Members)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        await _gate.WaitAsync();
        try { await WriteJsonAtomicAsync(GroupsPath, clean); }
        finally { _gate.Release(); }
    }

    public async Task<List<ScheduleDefinition>> GetSchedulesAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(SchedulesPath))
            {
                var empty = new List<ScheduleDefinition>();
                await WriteJsonAtomicAsync(SchedulesPath, empty);
                return empty;
            }
            return await ReadJsonAsync<List<ScheduleDefinition>>(SchedulesPath) ?? new();
        }
        finally { _gate.Release(); }
    }

    public async Task SetSchedulesAsync(List<ScheduleDefinition> schedules)
    {
        await _gate.WaitAsync();
        try { await WriteJsonAtomicAsync(SchedulesPath, schedules); }
        finally { _gate.Release(); }
    }

    public async Task AppendLogAsync(LogEntry entry)
    {
        // JSONL requires exactly one physical line per entry.
        // Keep indented JSON for settings, but disable it for audit entries.
        var line = JsonSerializer.Serialize(entry, new JsonSerializerOptions(_json)
        {
            WriteIndented = false
        });
        await _gate.WaitAsync();
        try { await File.AppendAllTextAsync(LogsPath, line + Environment.NewLine); }
        finally { _gate.Release(); }
    }

    public async Task<List<LogEntry>> GetLogsAsync(int take = 1000)
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(LogsPath)) return new();

            var content = await File.ReadAllTextAsync(LogsPath);
            return ParseLogEntries(content, Math.Clamp(take, 1, 5000));
        }
        finally { _gate.Release(); }
    }

    private List<LogEntry> ParseLogEntries(string content, int take)
    {
        // Older builds wrote indented JSON objects into logs.jsonl.
        // Read complete JSON values so both old multiline entries and new
        // one-line entries remain visible, without rewriting the audit file.
        var bytes = Encoding.UTF8.GetBytes(content.TrimStart('\uFEFF'));
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowMultipleValues = true
        });
        var entries = new Queue<LogEntry>();
        var invalidEntries = 0;

        void AddEntry(LogEntry entry)
        {
            entries.Enqueue(entry);
            while (entries.Count > take) entries.Dequeue();
        }

        try
        {
            while (reader.Read())
            {
                using var document = JsonDocument.ParseValue(ref reader);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    invalidEntries++;
                    continue;
                }

                try
                {
                    var entry = document.RootElement.Deserialize<LogEntry>(_json);
                    if (entry is not null) AddEntry(entry);
                }
                catch (JsonException)
                {
                    invalidEntries++;
                }
            }
        }
        catch (JsonException)
        {
            AddEntry(new LogEntry
            {
                Source = "SYSTEM",
                Event = "LOG_READ_WARNING",
                Target = "AUDIT LOG",
                Details = "Malformed or incomplete audit data was found. " +
                    "Complete entries before the error are shown. " +
                    "The original log file has been preserved."
            });
        }

        if (invalidEntries > 0)
        {
            AddEntry(new LogEntry
            {
                Source = "SYSTEM",
                Event = "LOG_READ_WARNING",
                Target = "AUDIT LOG",
                Details = $"Skipped {invalidEntries} JSON value(s) that were not valid log entries."
            });
        }

        return entries.Reverse().ToList();
    }


    public async Task<List<ClientHeartbeat>> GetKnownClientsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(ClientsPath)) return new();
            return await ReadJsonAsync<List<ClientHeartbeat>>(ClientsPath) ?? new();
        }
        finally { _gate.Release(); }
    }

    public async Task UpsertKnownClientAsync(ClientHeartbeat heartbeat)
    {
        await _gate.WaitAsync();
        try
        {
            var clients = File.Exists(ClientsPath)
                ? await ReadJsonAsync<List<ClientHeartbeat>>(ClientsPath) ?? new()
                : new List<ClientHeartbeat>();
            clients.RemoveAll(x => string.Equals(x.ComputerName, heartbeat.ComputerName, StringComparison.OrdinalIgnoreCase));
            clients.Add(heartbeat with { At = DateTimeOffset.Now });
            await WriteJsonAtomicAsync(ClientsPath, clients.OrderBy(x => x.ComputerName, StringComparer.OrdinalIgnoreCase).ToList());
        }
        finally { _gate.Release(); }
    }

    public async Task<MediaAsset> SaveMediaAsync(Stream source, string originalFileName, string contentType)
    {
        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
        if (!allowed.Contains(extension)) throw new InvalidOperationException("Unsupported image format.");

        var id = Guid.NewGuid().ToString("N");
        var storedName = id + extension;
        var fullPath = Path.Combine(MediaPath, storedName);

        await using (var output = File.Create(fullPath))
        {
            await source.CopyToAsync(output);
        }

        var info = new FileInfo(fullPath);
        var asset = new MediaAsset
        {
            Id = id,
            FileName = storedName,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            Length = info.Length,
            CreatedAt = DateTimeOffset.Now
        };

        var index = await GetMediaIndexInternalAsync();
        index.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        index.Add(asset);
        await WriteJsonAtomicAsync(MediaIndexPath, index);
        return asset;
    }

    public async Task<(MediaAsset Asset, string Path)?> GetMediaAsync(string id)
    {
        var index = await GetMediaIndexInternalAsync();
        var asset = index.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (asset is null) return null;
        var path = Path.Combine(MediaPath, asset.FileName);
        return File.Exists(path) ? (asset, path) : null;
    }

    private async Task<List<MediaAsset>> GetMediaIndexInternalAsync()
    {
        if (!File.Exists(MediaIndexPath)) return new();
        return await ReadJsonAsync<List<MediaAsset>>(MediaIndexPath) ?? new();
    }

    private async Task<T?> ReadJsonAsync<T>(string path)
    {
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await JsonSerializer.DeserializeAsync<T>(stream, _json);
    }

    private async Task WriteJsonAtomicAsync<T>(string path, T value)
    {
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, value, _json);
            await stream.FlushAsync();
        }
        File.Move(temp, path, true);
    }
}

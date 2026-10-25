using AutoShutdown.Shared;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace AutoShutdown.Client.Services;

public sealed class LocalSettingsCache
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;

    public LocalSettingsCache()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoShutdown.Nova", "SystemB");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "settings-cache.json");
    }

    public async Task<GlobalSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(_path)) return new GlobalSettings();
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<GlobalSettings>(stream, _json) ?? new GlobalSettings();
        }
        catch { return new GlobalSettings(); }
    }

    public async Task SaveAsync(GlobalSettings settings)
    {
        var temp = _path + ".tmp";
        await using (var stream = File.Create(temp)) await JsonSerializer.SerializeAsync(stream, settings, _json);
        File.Move(temp, _path, true);
    }
}

using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Schermpostvak.Api.Services;

/// <summary>Settings changed from the dashboard.</summary>
public record AppSettings
{
    /// <summary>Xibo library folder new media is uploaded to; null = the API user's home folder.</summary>
    public int? XiboFolderId { get; init; }
    public string? XiboFolderName { get; init; }
}

/// <summary>
/// Keeps dashboard settings in settings.json in the storage folder, so no database change is needed
/// and the file survives container updates.
/// </summary>
public class SettingsStore(IOptions<StorageOptions> storage, ILogger<SettingsStore> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path = Path.Combine(storage.Value.Root, "settings.json");
    private readonly Lock _lock = new();
    private AppSettings? _cached;

    public AppSettings Get()
    {
        lock (_lock)
        {
            if (_cached is not null) return _cached;
            try
            {
                _cached = File.Exists(_path)
                    ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json) ?? new AppSettings()
                    : new AppSettings();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not read {Path}; using defaults", _path);
                _cached = new AppSettings();
            }
            return _cached;
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
            File.Move(temp, _path, true);
            _cached = settings;
        }
    }
}

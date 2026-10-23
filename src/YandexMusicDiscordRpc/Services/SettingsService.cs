using System.Text.Json;
using System.Text.Json.Serialization;
using YandexMusicDiscordRpc.Constants;
using YandexMusicDiscordRpc.Models;

namespace YandexMusicDiscordRpc.Services;

public sealed class SettingsService
{
    private readonly AppLogger _logger;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public SettingsService(AppLogger logger)
    {
        _logger = logger;
        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppConstants.DataFolderName);
        SettingsPath = Path.Combine(DataDirectory, "settings.json");
    }

    public string DataDirectory { get; }
    public string SettingsPath { get; }
    public bool IsFirstRun { get; private set; }

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                if (!File.Exists(SettingsPath))
                {
                    IsFirstRun = true;
                    var defaults = new AppSettings();
                    defaults.Normalize();
                    return defaults;
                }

                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ?? new AppSettings();
                settings.Normalize();
                return settings;
            }
            catch (Exception ex)
            {
                _logger.Error("Не удалось прочитать настройки. Будут использованы значения по умолчанию.", ex);
                TryQuarantineBrokenSettings();
                IsFirstRun = true;
                var defaults = new AppSettings();
                defaults.Normalize();
                return defaults;
            }
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();

        string json;
        lock (_gate)
        {
            json = JsonSerializer.Serialize(settings, _jsonOptions);
        }

        Directory.CreateDirectory(DataDirectory);
        var tempPath = SettingsPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);

        try
        {
            File.Move(tempPath, SettingsPath, overwrite: true);
        }
        catch
        {
            File.Copy(tempPath, SettingsPath, overwrite: true);
            File.Delete(tempPath);
        }

        IsFirstRun = false;
    }

    private void TryQuarantineBrokenSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return;

            var brokenPath = Path.Combine(
                DataDirectory,
                $"settings.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(SettingsPath, brokenPath, overwrite: true);
        }
        catch
        {
            // Не критично.
        }
    }
}

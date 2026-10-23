using Microsoft.Win32;
using YandexMusicDiscordRpc.Constants;

namespace YandexMusicDiscordRpc.Services;

public sealed class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly AppLogger _logger;

    public AutostartService(AppLogger logger)
    {
        _logger = logger;
    }

    public void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
                return;

            if (enabled)
            {
                var executable = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executable))
                {
                    _logger.Warn("Не удалось определить путь к исполняемому файлу для автозапуска.");
                    return;
                }

                key.SetValue(
                    AppConstants.RunRegistryValueName,
                    $"\"{executable}\" --startup",
                    RegistryValueKind.String);
                _logger.Info("Автозапуск включён.");
            }
            else
            {
                key.DeleteValue(AppConstants.RunRegistryValueName, throwOnMissingValue: false);
                _logger.Info("Автозапуск выключен.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Не удалось изменить параметр автозапуска.", ex);
        }
    }
}

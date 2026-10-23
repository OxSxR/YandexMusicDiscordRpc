using System.Text;
using YandexMusicDiscordRpc.Constants;

namespace YandexMusicDiscordRpc.Services;

public sealed class AppLogger : IDisposable
{
    private const long MaxLogBytes = 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _logDirectory;
    private readonly string _logPath;
    private bool _disposed;

    public AppLogger()
    {
        _logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppConstants.DataFolderName,
            "logs");
        _logPath = Path.Combine(_logDirectory, "app.log");

        try
        {
            Directory.CreateDirectory(_logDirectory);
        }
        catch
        {
            // Логирование не должно мешать запуску приложения.
        }
    }

    public string LogPath => _logPath;

    public void Info(string message) => Write("INFO", message, null);
    public void Warn(string message) => Write("WARN", message, null);
    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        if (_disposed)
            return;

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_logDirectory);
                RotateIfNeeded();

                var builder = new StringBuilder(256);
                builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                builder.Append(" [").Append(level).Append("] ");
                builder.AppendLine(message);

                if (exception is not null)
                {
                    builder.AppendLine(exception.ToString());
                }

                File.AppendAllText(_logPath, builder.ToString(), new UTF8Encoding(false));
            }
        }
        catch
        {
            // Если диск недоступен или файл занят — работаем дальше.
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_logPath);
        if (!info.Exists || info.Length < MaxLogBytes)
            return;

        var backupPath = Path.Combine(_logDirectory, "app.1.log");
        File.Move(_logPath, backupPath, overwrite: true);
    }

    public void Dispose()
    {
        _disposed = true;
    }
}

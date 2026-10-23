using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using YandexMusicDiscordRpc.Constants;

namespace YandexMusicDiscordRpc.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly AppLogger _logger;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon? _icon;
    private bool _disposed;

    public TrayIconService(
        AppLogger logger,
        Action open,
        Action openSettings,
        Func<Task> restart,
        Action exit)
    {
        _logger = logger;

        _icon = LoadIcon();
        var menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Открыть");
        openItem.Font = new Font(openItem.Font, System.Drawing.FontStyle.Bold);
        openItem.Click += (_, _) => Dispatch(open);

        var settingsItem = new ToolStripMenuItem("Настройки");
        settingsItem.Click += (_, _) => Dispatch(openSettings);

        var restartItem = new ToolStripMenuItem("Перезапустить службу");
        restartItem.Click += (_, _) => Dispatch(() => _ = restart());

        var exitItem = new ToolStripMenuItem("Выход");
        exitItem.Click += (_, _) => Dispatch(exit);

        menu.Items.Add(openItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(restartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon ?? SystemIcons.Application,
            Text = AppConstants.AppName,
            Visible = true,
            ContextMenuStrip = menu
        };

        _notifyIcon.DoubleClick += (_, _) => Dispatch(open);
        _notifyIcon.BalloonTipClicked += (_, _) => Dispatch(open);
    }

    public void ShowBalloon(string title, string text)
    {
        try
        {
            _notifyIcon.ShowBalloonTip(4000, title, text, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Не удалось показать уведомление в трее: {ex.Message}");
        }
    }

    public void SetToolTip(string text)
    {
        try
        {
            _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
        }
        catch
        {
            // Игнорируем.
        }
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    private static Icon? LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute));
            if (resource?.Stream is not null)
            {
                using var stream = resource.Stream;
                return new Icon(stream);
            }
        }
        catch
        {
            // Пробуем извлечь иконку из exe.
        }

        try
        {
            var executable = Environment.ProcessPath;
            return executable is null ? null : Icon.ExtractAssociatedIcon(executable);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
            _icon?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Ошибка при закрытии значка в трее: {ex.Message}");
        }
    }
}

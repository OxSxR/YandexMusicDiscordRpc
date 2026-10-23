using System.Windows;
using System.Windows.Threading;
using YandexMusicDiscordRpc.Constants;
using YandexMusicDiscordRpc.Models;
using YandexMusicDiscordRpc.Services;
using YandexMusicDiscordRpc.ViewModels;

namespace YandexMusicDiscordRpc;

public partial class App : System.Windows.Application
{
    private readonly CancellationTokenSource _shutdownCts = new();

    private AppLogger? _logger;
    private SettingsService? _settingsService;
    private AutostartService? _autostart;
    private BrowserWindowProbe? _browserProbe;
    private MediaSessionService? _media;
    private YandexCatalogService? _catalog;
    private DiscordPresenceService? _discord;
    private PresenceCoordinator? _coordinator;
    private MainViewModel? _mainViewModel;
    private MainWindow? _mainWindow;
    private TrayIconService? _tray;
    private SingleInstanceService? _singleInstance;
    private SettingsWindow? _settingsWindow;
    private AppSettings _settings = new();
    private string? _lastTrayIdentity;
    private bool _isExiting;
    private bool _started;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _logger = new AppLogger();
        _logger.Info($"Запуск {AppConstants.AppName}. Версия {GetVersion()}.");

        RegisterUnhandledExceptionHandlers();

        _singleInstance = new SingleInstanceService(_logger, ActivateFromSecondInstance);
        if (!_singleInstance.IsPrimary)
        {
            _singleInstance.TryActivateExistingInstance();
            _singleInstance.Dispose();
            _singleInstance = null;
            _logger.Info("Обнаружен уже запущенный экземпляр. Активируем его окно.");
            Shutdown(0);
            return;
        }

        try
        {
            _settingsService = new SettingsService(_logger);
            _settings = _settingsService.Load();
            _settings.Normalize();

            if (_settingsService.IsFirstRun)
                await _settingsService.SaveAsync(_settings, _shutdownCts.Token).ConfigureAwait(true);

            _autostart = new AutostartService(_logger);
            _autostart.Apply(_settings.StartWithWindows);

            _browserProbe = new BrowserWindowProbe();
            _catalog = new YandexCatalogService(_logger, () => _settings);
            _media = new MediaSessionService(_logger, () => _settings, _browserProbe);
            _discord = new DiscordPresenceService(_logger, () => _settings);
            _coordinator = new PresenceCoordinator(
                _media,
                _discord,
                _catalog,
                _logger,
                () => _settings);

            _mainViewModel = new MainViewModel(_catalog, ShowSettings, RestartServiceAsync);
            _mainWindow = new MainWindow(_mainViewModel);
            _mainWindow.Closing += MainWindow_OnClosing;

            _tray = new TrayIconService(
                _logger,
                ShowMainWindow,
                ShowSettings,
                RestartServiceAsync,
                ExitApplication);

            _coordinator.SnapshotChanged += Coordinator_OnSnapshotChanged;
            _coordinator.TrackMetadataUpdated += Coordinator_OnTrackMetadataUpdated;
            _discord.StatusChanged += Discord_OnStatusChanged;

            _mainViewModel.ApplyDiscordStatus(
                _settings.EnableRichPresence ? DiscordStatus.Connecting : DiscordStatus.Disabled);

            _coordinator.Start();
            _started = true;

            if (_settings.StartMinimized && _settings.MinimizeToTray)
            {
                _tray.SetToolTip(AppConstants.AppName);
                _logger.Info("Приложение запущено свёрнутым в трей.");
            }
            else
            {
                ShowMainWindow();
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Критическая ошибка запуска приложения.", ex);
            System.Windows.MessageBox.Show(
                $"Не удалось запустить {AppConstants.AppName}.\n\n{ex.Message}\n\nПодробности: {_logger.LogPath}",
                AppConstants.AppName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _isExiting = true;
            Shutdown(1);
        }
    }

    private void RegisterUnhandledExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            _logger?.Error("Необработанная ошибка интерфейса.", args.Exception);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                _logger?.Error("Необработанная ошибка приложения.", exception);
            else
                _logger?.Error($"Необработанная ошибка приложения: {args.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.Error("Необработанная ошибка фоновой задачи.", args.Exception);
            args.SetObserved();
        };
    }

    private void Coordinator_OnSnapshotChanged(object? sender, PlaybackSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (_isExiting || _mainViewModel is null)
                    return;

                _mainViewModel.ApplySnapshot(snapshot);

                if (snapshot.Track is { } track)
                {
                    if (!string.Equals(_lastTrayIdentity, track.Identity, StringComparison.Ordinal))
                    {
                        _lastTrayIdentity = track.Identity;
                        var artist = string.IsNullOrWhiteSpace(track.Artist) ? "Яндекс Музыка" : track.Artist;
                        _tray?.SetToolTip($"{track.Title} — {artist}");
                    }
                }
                else if (_lastTrayIdentity is not null)
                {
                    _lastTrayIdentity = null;
                    _tray?.SetToolTip(AppConstants.AppName);
                }
            }));
    }

    private void Coordinator_OnTrackMetadataUpdated(object? sender, TrackInfo track)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (!_isExiting)
                    _mainViewModel?.ApplyTrackMetadata(track);
            }));
    }

    private void Discord_OnStatusChanged(object? sender, DiscordStatus status)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (!_isExiting)
                    _mainViewModel?.ApplyDiscordStatus(status);
            }));
    }

    private void ShowMainWindow()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ShowMainWindow));
            return;
        }

        if (_isExiting || !_started || _mainWindow is null)
            return;

        if (!_mainWindow.IsVisible)
            _mainWindow.Show();

        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;

        _mainWindow.Activate();

        try
        {
            _mainWindow.Topmost = true;
            _mainWindow.Topmost = false;
            _mainWindow.Focus();
        }
        catch
        {
            // Фокус не критичен.
        }
    }

    private void ShowSettings()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ShowSettings));
            return;
        }

        if (_isExiting || _started is false)
            return;

        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        var window = new SettingsWindow(_settings);
        _settingsWindow = window;

        if (_mainWindow is { IsVisible: true })
            window.Owner = _mainWindow;
        else
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        window.Saved += SettingsWindow_OnSaved;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsWindow, window))
                _settingsWindow = null;
        };

        window.ShowDialog();
    }

    private async void SettingsWindow_OnSaved(object? sender, AppSettings settings)
    {
        if (_isExiting)
            return;

        try
        {
            _settings = settings.Clone();
            _settings.Normalize();

            if (_settingsService is not null)
                await _settingsService.SaveAsync(_settings, _shutdownCts.Token).ConfigureAwait(true);

            _autostart?.Apply(_settings.StartWithWindows);
            _coordinator?.ApplySettings();

            _logger?.Info("Настройки сохранены.");
        }
        catch (Exception ex)
        {
            _logger?.Error("Не удалось сохранить настройки.", ex);
            System.Windows.MessageBox.Show(
                $"Не удалось сохранить настройки.\n\n{ex.Message}",
                AppConstants.AppName,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task RestartServiceAsync()
    {
        if (_coordinator is null || _isExiting)
            return;

        try
        {
            await _coordinator.RestartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Error("Ошибка при перезапуске службы.", ex);
        }
    }

    private void MainWindow_OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting)
            return;

        if (_settings.MinimizeToTray)
        {
            e.Cancel = true;
            _mainWindow?.Hide();

            if (_settings.ShowFirstTrayHint)
            {
                _settings.ShowFirstTrayHint = false;
                _tray?.ShowBalloon(
                    AppConstants.AppName,
                    "Приложение продолжает работать в трее. Статус в Discord обновляется автоматически.");
            }
        }
        else
        {
            _isExiting = true;
            Shutdown();
        }
    }

    private void ExitApplication()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ExitApplication));
            return;
        }

        _isExiting = true;
        Shutdown();
    }

    private void ActivateFromSecondInstance()
    {
        Dispatcher.BeginInvoke(new Action(ShowMainWindow));
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // При завершении сеанса Windows не прячем окно в трей и не мешаем выходу.
        _isExiting = true;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isExiting = true;
        _started = false;
        _shutdownCts.Cancel();

        try
        {
            if (_coordinator is not null)
            {
                _coordinator.SnapshotChanged -= Coordinator_OnSnapshotChanged;
                _coordinator.TrackMetadataUpdated -= Coordinator_OnTrackMetadataUpdated;
            }

            if (_discord is not null)
                _discord.StatusChanged -= Discord_OnStatusChanged;
        }
        catch
        {
            // Игнорируем ошибки отписки.
        }

        try
        {
            _tray?.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Ошибка при закрытии трея: {ex.Message}");
        }

        try
        {
            _mainViewModel?.Dispose();
        }
        catch
        {
            // Игнорируем.
        }

        try
        {
            _settingsWindow?.Close();
        }
        catch
        {
            // Окно могло быть уже закрыто.
        }

        if (_coordinator is not null)
        {
            try
            {
                Task.Run(async () => await _coordinator.DisposeAsync().ConfigureAwait(false))
                    .Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Ошибка при остановке служб: {ex.Message}");
            }
        }

        try
        {
            _catalog?.Dispose();
        }
        catch
        {
            // Игнорируем.
        }

        try
        {
            _singleInstance?.Dispose();
        }
        catch
        {
            // Игнорируем.
        }

        _logger?.Info("Приложение завершено.");
        _logger?.Dispose();
        _shutdownCts.Dispose();

        base.OnExit(e);
    }

    private static string GetVersion()
    {
        return typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }
}

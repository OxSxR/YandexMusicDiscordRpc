using YandexMusicDiscordRpc.Constants;
using YandexMusicDiscordRpc.Models;

namespace YandexMusicDiscordRpc.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _original;
    private bool _startWithWindows;
    private bool _minimizeToTray;
    private bool _startMinimized;
    private bool _showCover;
    private bool _showProgress;
    private bool _showButtons;
    private bool _enrichFromCatalog;
    private SourcePreference _sourcePreference;
    private StatusDisplayPreference _statusDisplay;

    public SettingsViewModel(AppSettings settings)
    {
        _original = settings.Clone();
        _startWithWindows = settings.StartWithWindows;
        _minimizeToTray = settings.MinimizeToTray;
        _startMinimized = settings.StartMinimized;
        _showCover = settings.ShowCover;
        _showProgress = settings.ShowProgress;
        _showButtons = settings.ShowButtons;
        _enrichFromCatalog = settings.EnrichFromCatalog;
        _sourcePreference = settings.SourcePreference;
        _statusDisplay = settings.StatusDisplay;
    }

    public string VersionText { get; } = $"Версия {GetVersion()}";

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set
        {
            if (SetProperty(ref _minimizeToTray, value))
                RaisePropertyChanged(nameof(StartMinimizedEnabled));
        }
    }

    public bool StartMinimized
    {
        get => _startMinimized;
        set => SetProperty(ref _startMinimized, value);
    }

    public bool StartMinimizedEnabled => MinimizeToTray;

    public bool ShowCover
    {
        get => _showCover;
        set => SetProperty(ref _showCover, value);
    }

    public bool ShowProgress
    {
        get => _showProgress;
        set => SetProperty(ref _showProgress, value);
    }

    public bool ShowButtons
    {
        get => _showButtons;
        set => SetProperty(ref _showButtons, value);
    }

    public bool EnrichFromCatalog
    {
        get => _enrichFromCatalog;
        set => SetProperty(ref _enrichFromCatalog, value);
    }

    public StatusDisplayPreference StatusDisplay
    {
        get => _statusDisplay;
        set
        {
            if (!SetProperty(ref _statusDisplay, value))
                return;

            RaisePropertyChanged(nameof(ShowTrackNameInStatus));
        }
    }

    public bool ShowTrackNameInStatus
    {
        get => StatusDisplay == StatusDisplayPreference.TrackName;
        set => StatusDisplay = value
            ? StatusDisplayPreference.TrackName
            : StatusDisplayPreference.ApplicationName;
    }

    public SourcePreference SourcePreference
    {
        get => _sourcePreference;
        set
        {
            if (!SetProperty(ref _sourcePreference, value))
                return;

            RaisePropertyChanged(nameof(UseAutoSource));
            RaisePropertyChanged(nameof(UseWebSource));
            RaisePropertyChanged(nameof(UseDesktopSource));
        }
    }

    public bool UseAutoSource
    {
        get => SourcePreference == SourcePreference.Auto;
        set
        {
            if (value)
                SourcePreference = SourcePreference.Auto;
        }
    }

    public bool UseWebSource
    {
        get => SourcePreference == SourcePreference.Web;
        set
        {
            if (value)
                SourcePreference = SourcePreference.Web;
        }
    }

    public bool UseDesktopSource
    {
        get => SourcePreference == SourcePreference.Desktop;
        set
        {
            if (value)
                SourcePreference = SourcePreference.Desktop;
        }
    }

    public bool TryCreateSettings(out AppSettings settings)
    {
        settings = _original.Clone();
        settings.StartWithWindows = StartWithWindows;
        settings.MinimizeToTray = MinimizeToTray;
        settings.StartMinimized = MinimizeToTray && StartMinimized;
        settings.EnableRichPresence = true;
        settings.ShowCover = ShowCover;
        settings.ShowProgress = ShowProgress;
        settings.ShowButtons = ShowButtons;
        settings.StatusDisplay = StatusDisplay;
        settings.EnrichFromCatalog = EnrichFromCatalog;
        settings.DiscordClientId = AppConstants.DefaultDiscordClientId;
        settings.SourcePreference = SourcePreference;

        settings.Normalize();
        return true;
    }

    private static string GetVersion()
    {
        return typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.2";
    }
}

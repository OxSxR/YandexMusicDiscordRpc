using System.Diagnostics;
using System.Reflection;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YandexMusicDiscordRpc.Models;
using YandexMusicDiscordRpc.Services;

namespace YandexMusicDiscordRpc.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly YandexCatalogService _catalog;
    private readonly Func<Task> _restartService;
    private readonly DispatcherTimer _clock;
    private readonly CancellationTokenSource _lifetimeCts = new();

    private TrackInfo? _track;
    private BitmapImage? _coverImage;
    private byte[]? _lastArtworkBytes;
    private string? _loadedCoverUrl;
    private CancellationTokenSource? _coverCts;
    private string? _coverIdentity;

    private string _trackTitle = "Ничего не играет";
    private string _trackArtist = "Запустите Яндекс Музыку";
    private string _trackAlbum = string.Empty;
    private string _playbackStateText = "Ожидание";
    private string _sourceText = "Источник не определён";
    private string _positionText = "00:00";
    private string _durationText = "00:00";
    private string _trackTimeText = "00:00 / 00:00";
    private string _statusText = "Яндекс Музыка не запущена";
    private string _discordStatusText = DiscordStatus.Disconnected.Text;
    private double _progressValue;
    private bool _hasTrack;
    private bool _hasCover;
    private bool _hasAlbum;
    private bool _isPlaying;
    private bool _isRestarting;
    private bool _hasTrackUrl;
    private bool _hasArtistUrl;
    private bool _isMusicRunning;
    private DiscordConnectionState _discordState = DiscordConnectionState.Disconnected;
    private string _restartStatusText = string.Empty;
    private bool _disposed;

    public MainViewModel(
        YandexCatalogService catalog,
        Action openSettings,
        Func<Task> restartService)
    {
        _catalog = catalog;
        _restartService = restartService;

        OpenSettingsCommand = new RelayCommand(openSettings);
        RestartServiceCommand = new AsyncRelayCommand(RestartServiceAsync);
        OpenTrackCommand = new RelayCommand(OpenTrack, () => HasTrackUrl);
        OpenArtistCommand = new RelayCommand(OpenArtist, () => HasArtistUrl);
        DismissRestartStatusCommand = new RelayCommand(() => RestartStatusText = string.Empty);

        _clock = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _clock.Tick += (_, _) => Tick();
    }

    public RelayCommand OpenSettingsCommand { get; }
    public AsyncRelayCommand RestartServiceCommand { get; }
    public RelayCommand OpenTrackCommand { get; }
    public RelayCommand OpenArtistCommand { get; }
    public RelayCommand DismissRestartStatusCommand { get; }

    public string VersionText { get; } = $"v{GetVersion()}";

    public string TrackTitle
    {
        get => _trackTitle;
        private set => SetProperty(ref _trackTitle, value);
    }

    public string TrackArtist
    {
        get => _trackArtist;
        private set => SetProperty(ref _trackArtist, value);
    }

    public string TrackAlbum
    {
        get => _trackAlbum;
        private set => SetProperty(ref _trackAlbum, value);
    }

    public string PlaybackStateText
    {
        get => _playbackStateText;
        private set => SetProperty(ref _playbackStateText, value);
    }

    public string SourceText
    {
        get => _sourceText;
        private set => SetProperty(ref _sourceText, value);
    }

    public string PositionText
    {
        get => _positionText;
        private set => SetProperty(ref _positionText, value);
    }

    public string DurationText
    {
        get => _durationText;
        private set => SetProperty(ref _durationText, value);
    }

    public string TrackTimeText
    {
        get => _trackTimeText;
        private set => SetProperty(ref _trackTimeText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string DiscordStatusText
    {
        get => _discordStatusText;
        private set => SetProperty(ref _discordStatusText, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    public bool HasTrack
    {
        get => _hasTrack;
        private set => SetProperty(ref _hasTrack, value);
    }

    public bool HasCover
    {
        get => _hasCover;
        private set => SetProperty(ref _hasCover, value);
    }

    public bool HasAlbum
    {
        get => _hasAlbum;
        private set => SetProperty(ref _hasAlbum, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetProperty(ref _isPlaying, value);
    }

    public bool IsRestarting
    {
        get => _isRestarting;
        private set => SetProperty(ref _isRestarting, value);
    }

    public bool HasTrackUrl
    {
        get => _hasTrackUrl;
        private set
        {
            if (SetProperty(ref _hasTrackUrl, value))
                OpenTrackCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasArtistUrl
    {
        get => _hasArtistUrl;
        private set
        {
            if (SetProperty(ref _hasArtistUrl, value))
                OpenArtistCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsMusicRunning
    {
        get => _isMusicRunning;
        private set => SetProperty(ref _isMusicRunning, value);
    }

    public BitmapImage? CoverImage
    {
        get => _coverImage;
        private set
        {
            if (SetProperty(ref _coverImage, value))
                HasCover = value is not null;
        }
    }

    public DiscordConnectionState DiscordState
    {
        get => _discordState;
        private set => SetProperty(ref _discordState, value);
    }

    public string RestartStatusText
    {
        get => _restartStatusText;
        private set
        {
            if (SetProperty(ref _restartStatusText, value))
                RaisePropertyChanged(nameof(HasRestartStatus));
        }
    }

    public bool HasRestartStatus => !string.IsNullOrWhiteSpace(RestartStatusText);

    public void StartClock() => _clock.Start();
    public void StopClock() => _clock.Stop();

    public void ApplySnapshot(PlaybackSnapshot snapshot)
    {
        IsMusicRunning = snapshot.IsYandexMusicRunning;
        StatusText = string.IsNullOrWhiteSpace(snapshot.StatusText)
            ? "Нет данных о воспроизведении"
            : snapshot.StatusText;

        if (snapshot.Track is null)
        {
            ClearTrack();
            return;
        }

        ApplyTrack(snapshot.Track);
    }

    public void ApplyTrackMetadata(TrackInfo track)
    {
        if (_track is not null &&
            !string.Equals(_track.Identity, track.Identity, StringComparison.Ordinal))
        {
            return;
        }

        ApplyTrack(track);
    }

    public void ApplyDiscordStatus(DiscordStatus status)
    {
        DiscordState = status.State;
        DiscordStatusText = status.Text;
    }

    private void ApplyTrack(TrackInfo track)
    {
        var trackChanged = _track is null ||
                           !string.Equals(_track.Identity, track.Identity, StringComparison.Ordinal);
        var previousArtwork = _track?.Artwork;

        _track = track;

        if (trackChanged)
        {
            _coverIdentity = track.Identity;
            _loadedCoverUrl = null;
            _lastArtworkBytes = null;
            CancelCoverLoad();
        }

        HasTrack = true;
        TrackTitle = track.Title;
        TrackArtist = string.IsNullOrWhiteSpace(track.Artist) ? "Неизвестный исполнитель" : track.Artist;
        TrackAlbum = track.Album;
        HasAlbum = !string.IsNullOrWhiteSpace(track.Album);
        IsPlaying = track.IsPlaying;
        PlaybackStateText = track.PlaybackState switch
        {
            PlaybackState.Playing => "▶  Воспроизведение",
            PlaybackState.Paused => "⏸  Пауза",
            _ => "Остановлено"
        };
        SourceText = track.Source switch
        {
            MusicSource.Desktop => "Приложение Яндекс Музыки",
            MusicSource.Web => "Яндекс Музыка в браузере",
            _ => "Источник не определён"
        };
        HasTrackUrl = !string.IsNullOrWhiteSpace(track.TrackUrl);
        HasArtistUrl = !string.IsNullOrWhiteSpace(track.ArtistUrl);

        UpdatePosition(track.GetPositionAt(DateTimeOffset.UtcNow));
        UpdateArtwork(track, trackChanged, previousArtwork);
    }

    private void UpdatePosition(TimeSpan position)
    {
        var duration = _track?.Duration ?? TimeSpan.Zero;
        if (duration > TimeSpan.Zero && position > duration)
            position = duration;
        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;

        PositionText = FormatTime(position);
        DurationText = FormatTime(duration);
        TrackTimeText = duration > TimeSpan.Zero
            ? $"{PositionText} / {DurationText}"
            : PositionText;
        ProgressValue = duration > TimeSpan.Zero
            ? Math.Clamp(position.TotalMilliseconds / duration.TotalMilliseconds * 100.0, 0, 100)
            : 0;
    }

    private void UpdateArtwork(TrackInfo track, bool trackChanged, byte[]? previousArtwork)
    {
        if (track.Artwork is { Length: > 0 })
        {
            if (trackChanged || !ReferenceEquals(previousArtwork, track.Artwork) ||
                !ReferenceEquals(_lastArtworkBytes, track.Artwork))
            {
                _lastArtworkBytes = track.Artwork;
                _loadedCoverUrl = null;
                SetCoverFromBytes(track.Artwork);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(track.CoverUrl))
        {
            if (trackChanged)
                CoverImage = null;
            return;
        }

        if (!string.Equals(_loadedCoverUrl, track.CoverUrl, StringComparison.Ordinal))
        {
            _loadedCoverUrl = track.CoverUrl;
            _ = LoadCoverFromUrlAsync(track.CoverUrl, track.Identity, _lifetimeCts.Token);
        }
    }

    private async Task LoadCoverFromUrlAsync(string url, string identity, CancellationToken lifetimeToken)
    {
        CancelCoverLoad();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        _coverCts = cts;

        try
        {
            var bytes = await _catalog.GetCoverBytesAsync(url, cts.Token);
            if (bytes is null || cts.IsCancellationRequested)
                return;

            if (!string.Equals(_coverIdentity, identity, StringComparison.Ordinal))
                return;

            SetCoverFromBytes(bytes);
        }
        catch (OperationCanceledException)
        {
            // Трек сменился.
        }
        finally
        {
            if (ReferenceEquals(_coverCts, cts))
                _coverCts = null;
            cts.Dispose();
        }
    }

    private void SetCoverFromBytes(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 320;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            CoverImage = image;
        }
        catch
        {
            CoverImage = null;
        }
    }

    private void ClearTrack()
    {
        if (_track is null && !HasTrack)
            return;

        _track = null;
        _lastArtworkBytes = null;
        _loadedCoverUrl = null;
        _coverIdentity = null;
        CancelCoverLoad();

        HasTrack = false;
        HasAlbum = false;
        HasTrackUrl = false;
        HasArtistUrl = false;
        IsPlaying = false;
        TrackTitle = "Ничего не играет";
        TrackArtist = "Запустите Яндекс Музыку";
        TrackAlbum = string.Empty;
        PlaybackStateText = "Нет активного воспроизведения";
        SourceText = "Источник не определён";
        ProgressValue = 0;
        PositionText = "00:00";
        DurationText = "00:00";
        TrackTimeText = "00:00 / 00:00";
        CoverImage = null;
    }

    private void Tick()
    {
        if (_track is null)
            return;

        UpdatePosition(_track.GetPositionAt(DateTimeOffset.UtcNow));
    }

    private async Task RestartServiceAsync()
    {
        if (IsRestarting)
            return;

        IsRestarting = true;
        RestartStatusText = "Перезапуск службы…";
        try
        {
            await _restartService();
            RestartStatusText = "Служба перезапущена";
        }
        catch (Exception ex)
        {
            RestartStatusText = $"Не удалось перезапустить службу: {ex.Message}";
        }
        finally
        {
            IsRestarting = false;
        }
    }

    private void OpenTrack()
    {
        if (_track?.TrackUrl is { Length: > 0 } url)
            OpenUrl(url);
    }

    private void OpenArtist()
    {
        if (_track?.ArtistUrl is { Length: > 0 } url)
            OpenUrl(url);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Ссылка могла устареть — приложение не должно падать.
        }
    }

    private static string FormatTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
    }

    private static string GetVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.2";
    }

    private void CancelCoverLoad()
    {
        var cts = _coverCts;
        _coverCts = null;
        if (cts is null)
            return;

        try
        {
            cts.Cancel();
        }
        catch
        {
            // Игнорируем.
        }

        cts.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopClock();
        CancelCoverLoad();
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
    }
}

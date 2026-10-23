using System.Text;
using DiscordRPC;
using DiscordRPC.Logging;
using DiscordRPC.Message;
using YandexMusicDiscordRpc.Constants;
using YandexMusicDiscordRpc.Models;
using DiscordButton = DiscordRPC.Button;

namespace YandexMusicDiscordRpc.Services;

/// <summary>
/// Единая точка обновления Discord Rich Presence. Обновления ограничены
/// одним запросом в секунду, переподключение выполняет DiscordRPC + наш сторож.
/// </summary>
public sealed class DiscordPresenceService : IAsyncDisposable
{
    private readonly AppLogger _logger;
    private readonly Func<AppSettings> _settingsProvider;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _wakeup = new(0, 1);

    private CancellationTokenSource? _cts;
    private Task? _worker;
    private DiscordRpcClient? _client;
    private string? _clientId;
    private bool _ready;
    private bool _presenceSet;
    private TrackInfo? _track;
    private AppSettings _settings = new();
    private DiscordStatus _status = DiscordStatus.Disconnected;
    private DateTimeOffset _lastSentAt = DateTimeOffset.MinValue;
    private DateTimeOffset _clientStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDisconnectedLogAt = DateTimeOffset.MinValue;
    private string _lastPresenceKey = string.Empty;
    private bool _restartRequested;
    private bool _disposed;

    public DiscordPresenceService(AppLogger logger, Func<AppSettings> settingsProvider)
    {
        _logger = logger;
        _settingsProvider = settingsProvider;
    }

    public event EventHandler<DiscordStatus>? StatusChanged;

    public DiscordStatus Status
    {
        get
        {
            lock (_sync)
                return _status;
        }
    }

    public void Start()
    {
        if (_disposed || _worker is not null)
            return;

        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => WorkerLoopAsync(_cts.Token));
        _logger.Info("Служба Discord Rich Presence запущена.");
    }

    public void Update(TrackInfo? track, AppSettings settings)
    {
        lock (_sync)
        {
            _track = track;
            _settings = settings.Clone();
        }

        SignalWakeup();
    }

    public void Restart()
    {
        lock (_sync)
        {
            _restartRequested = true;
            _lastPresenceKey = string.Empty;
            _lastSentAt = DateTimeOffset.MinValue;
        }

        SignalWakeup();
    }

    private void SignalWakeup()
    {
        try
        {
            if (_wakeup.CurrentCount == 0)
                _wakeup.Release();
        }
        catch (SemaphoreFullException)
        {
            // Уже есть запланированное пробуждение.
        }
    }

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Error("Ошибка в службе Discord Rich Presence.", ex);
            }

            try
            {
                await _wakeup.WaitAsync(TimeSpan.FromMilliseconds(1000), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        AppSettings settings;
        TrackInfo? track;
        var restart = false;

        lock (_sync)
        {
            settings = _settings.Clone();
            track = _track;
            if (_restartRequested)
            {
                _restartRequested = false;
                restart = true;
            }
        }

        if (!settings.EnableRichPresence)
        {
            ClearPresenceIfPossible();
            DisposeClient();
            SetStatus(DiscordStatus.Disabled);
            return;
        }

        if (restart)
        {
            DisposeClient();
            SetStatus(DiscordStatus.Connecting);
        }

        if (_client is null || !string.Equals(_clientId, settings.DiscordClientId, StringComparison.Ordinal))
        {
            CreateClient(settings.DiscordClientId);
        }

        if (_client is null)
            return;

        if (!_ready)
        {
            // DiscordRPC сам переподключается, но его backoff может вырасти до минуты.
            // Периодически пересоздаём клиент, чтобы после запуска Discord подключиться быстро.
            if (DateTimeOffset.UtcNow - _clientStartedAt > TimeSpan.FromSeconds(AppConstants.DiscordConnectionResetSeconds))
            {
                DisposeClient();
                CreateClient(settings.DiscordClientId);
            }

            return;
        }

        if (track is null)
        {
            ClearPresenceIfPossible();
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastSentAt < TimeSpan.FromMilliseconds(AppConstants.DiscordMinimumUpdateIntervalMilliseconds))
        {
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            return;
        }

        RichPresence presence;
        string key;
        try
        {
            (presence, key) = BuildPresence(track, settings, now);
        }
        catch (Exception ex)
        {
            _logger.Error("Не удалось собрать данные для Discord Rich Presence.", ex);
            SetStatus(DiscordStatus.Error);
            return;
        }

        var shouldRefresh = now - _lastSentAt >= TimeSpan.FromSeconds(AppConstants.DiscordPeriodicRefreshSeconds);
        if (string.Equals(key, _lastPresenceKey, StringComparison.Ordinal) && !shouldRefresh)
            return;

        try
        {
            _client.SetPresence(presence);
            _presenceSet = true;
            _lastPresenceKey = key;
            _lastSentAt = now;
        }
        catch (Exception ex)
        {
            _logger.Error("Discord отклонил обновление Rich Presence.", ex);
            _lastPresenceKey = string.Empty;
        }
    }

    private void CreateClient(string clientId)
    {
        DisposeClient();

        if (!AppSettings.IsValidDiscordClientId(clientId))
        {
            SetStatus(new DiscordStatus(DiscordConnectionState.Error, "Некорректный Discord Application ID"));
            _logger.Error($"Некорректный Discord Application ID: «{clientId}».");
            return;
        }

        try
        {
            var client = new DiscordRpcClient(clientId, -1, new NullLogger(), autoEvents: true)
            {
                SkipIdenticalPresence = true,
                ShutdownOnly = true
            };

            client.OnReady += OnReady;
            client.OnConnectionFailed += OnConnectionFailed;
            client.OnClose += OnClose;
            client.OnError += OnError;

            _client = client;
            _clientId = clientId;
            _ready = false;
            _presenceSet = false;
            _lastPresenceKey = string.Empty;
            _lastSentAt = DateTimeOffset.MinValue;
            _clientStartedAt = DateTimeOffset.UtcNow;

            SetStatus(DiscordStatus.Connecting);
            _logger.Info($"Подключение к Discord (Application ID: {clientId}).");
            client.Initialize();
        }
        catch (Exception ex)
        {
            _logger.Error("Не удалось инициализировать Discord RPC.", ex);
            SetStatus(DiscordStatus.Error);
            DisposeClient();
        }
    }

    private void DisposeClient()
    {
        var client = _client;
        _client = null;
        _clientId = null;
        _ready = false;
        _presenceSet = false;
        _lastPresenceKey = string.Empty;

        if (client is null)
            return;

        try
        {
            client.OnReady -= OnReady;
            client.OnConnectionFailed -= OnConnectionFailed;
            client.OnClose -= OnClose;
            client.OnError -= OnError;
            client.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Ошибка при отключении Discord RPC: {ex.Message}");
        }
    }

    private void OnReady(object sender, ReadyMessage message)
    {
        lock (_sync)
        {
            _ready = true;
            _presenceSet = false;
            _lastPresenceKey = string.Empty;
        }

        _logger.Info($"Discord подключён как {message.User?.Username ?? "пользователь"}.");
        SetStatus(new DiscordStatus(DiscordConnectionState.Connected, "подключен"));
        SignalWakeup();
    }

    private void OnConnectionFailed(object sender, ConnectionFailedMessage message)
    {
        lock (_sync)
        {
            _ready = false;
            _presenceSet = false;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastDisconnectedLogAt > TimeSpan.FromSeconds(30))
        {
            _lastDisconnectedLogAt = now;
            _logger.Info("Discord не запущен или RPC недоступен. Повторяем подключение в фоне.");
        }

        SetStatus(DiscordStatus.Disconnected);
        SignalWakeup();
    }

    private void OnClose(object sender, CloseMessage message)
    {
        lock (_sync)
        {
            _ready = false;
            _presenceSet = false;
            _lastPresenceKey = string.Empty;
        }

        _logger.Info($"Соединение с Discord закрыто: {message.Reason}");
        SetStatus(DiscordStatus.Disconnected);
        SignalWakeup();
    }

    private void OnError(object sender, ErrorMessage message)
    {
        _logger.Warn($"Ошибка Discord RPC ({message.Code}): {message.Message}");
        if (!_ready)
            SetStatus(DiscordStatus.Error);
    }

    private void ClearPresenceIfPossible()
    {
        var client = _client;
        if (client is null || !_ready)
        {
            _presenceSet = false;
            return;
        }

        try
        {
            if (_presenceSet)
                client.ClearPresence();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Не удалось очистить статус Discord: {ex.Message}");
        }
        finally
        {
            _presenceSet = false;
            _lastPresenceKey = string.Empty;
        }
    }

    private (RichPresence Presence, string Key) BuildPresence(
        TrackInfo track,
        AppSettings settings,
        DateTimeOffset now)
    {
        var details = FitText(track.Title, "Яндекс Музыка");
        var state = FitText(
            string.IsNullOrWhiteSpace(track.Artist) ? AppConstants.DiscordApplicationName : track.Artist,
            AppConstants.DiscordApplicationName);

        var presence = new RichPresence
        {
            Type = ActivityType.Listening,
            StatusDisplay = settings.StatusDisplay == StatusDisplayPreference.TrackName
                ? StatusDisplayType.Details
                : StatusDisplayType.Name,
            Details = details,
            State = state,
            DetailsUrl = SafeUrl(track.TrackUrl, 256),
            StateUrl = SafeUrl(track.ArtistUrl, 256),
            Buttons = settings.ShowButtons ? BuildButtons(track) : null,
            Assets = settings.ShowCover ? BuildAssets(track) : null
        };

        if (settings.ShowProgress && track.IsPlaying)
        {
            var position = track.GetPositionAt(now);
            var start = now.UtcDateTime - position;
            var end = track.Duration > TimeSpan.Zero ? start + track.Duration : (DateTime?)null;
            presence.Timestamps = new Timestamps
            {
                Start = start,
                End = end
            };
        }

        var key = string.Join('\u001f',
            track.Identity,
            track.PlaybackState,
            settings.ShowCover,
            settings.ShowProgress,
            settings.ShowButtons,
            settings.StatusDisplay.ToString(),
            presence.Details,
            presence.State,
            presence.DetailsUrl,
            presence.StateUrl,
            presence.Assets?.LargeImageKey,
            presence.Assets?.SmallImageKey,
            presence.Assets?.LargeImageText,
            presence.Buttons is null ? string.Empty : string.Join('|', presence.Buttons.Select(b => $"{b.Label}={b.Url}")));

        return (presence, key);
    }

    private static Assets? BuildAssets(TrackInfo track)
    {
        var cover = SafeUrl(track.CoverUrl, 256);
        var largeKey = cover ?? AppConstants.DiscordAppIconUrl;
        var albumText = FitText(
            string.IsNullOrWhiteSpace(track.Album) ? AppConstants.DiscordApplicationName : track.Album,
            AppConstants.DiscordApplicationName);

        var assets = new Assets
        {
            LargeImageKey = largeKey,
            LargeImageText = albumText,
            LargeImageUrl = SafeUrl(track.AlbumUrl ?? track.TrackUrl, 256)
        };

        if (cover is not null)
        {
            assets.SmallImageKey = AppConstants.DiscordAppIconUrl;
            assets.SmallImageText = AppConstants.DiscordApplicationName;
        }

        return assets;
    }

    private static DiscordButton[]? BuildButtons(TrackInfo track)
    {
        var buttons = new List<DiscordButton>(2);
        if (SafeUrl(track.TrackUrl, 512) is { } trackUrl)
        {
            buttons.Add(new DiscordButton
            {
                Label = FitButtonLabel("Открыть трек"),
                Url = trackUrl
            });
        }

        if (SafeUrl(track.ArtistUrl, 512) is { } artistUrl)
        {
            buttons.Add(new DiscordButton
            {
                Label = FitButtonLabel("Исполнитель"),
                Url = artistUrl
            });
        }

        return buttons.Count == 0 ? null : buttons.ToArray();
    }

    private static string FitText(string? value, string fallback)
    {
        var text = Sanitize(value);
        if (text.Length == 0)
            text = fallback;

        if (text.Length > AppConstants.MaxPresenceTextLength)
            text = text[..AppConstants.MaxPresenceTextLength].TrimEnd();

        if (text.Length < 2)
            text += "\u2800";

        return text;
    }

    private static string FitButtonLabel(string value)
    {
        var bytes = Encoding.UTF8.GetByteCount(value);
        if (bytes <= AppConstants.MaxButtonLabelBytes)
            return value;

        var builder = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (Encoding.UTF8.GetByteCount(builder.ToString()) + rune.Utf8SequenceLength >
                AppConstants.MaxButtonLabelBytes)
            {
                break;
            }

            builder.Append(rune.ToString());
        }

        return builder.Length == 0 ? "..." : builder.ToString();
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsControl(character))
                builder.Append(' ');
            else
                builder.Append(character);
        }

        return string.Join(' ', builder.ToString().Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? SafeUrl(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var url = value.Trim();
        if (url.Length > maxLength)
            return null;

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? url
            : null;
    }

    private void SetStatus(DiscordStatus status)
    {
        lock (_sync)
        {
            if (_status == status)
                return;
            _status = status;
        }

        try
        {
            StatusChanged?.Invoke(this, status);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Ошибка обработчика статуса Discord: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        var cts = _cts;
        _cts = null;
        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            if (_worker is not null)
            {
                try
                {
                    await _worker.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
                catch
                {
                    // Завершаемся в любом случае.
                }
            }
        }

        _worker = null;
        ClearPresenceIfPossible();
        DisposeClient();
        _wakeup.Dispose();
        cts?.Dispose();
    }
}

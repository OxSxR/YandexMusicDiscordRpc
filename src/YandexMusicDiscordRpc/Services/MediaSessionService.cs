using System.IO;
using YandexMusicDiscordRpc.Constants;
using YandexMusicDiscordRpc.Models;
using Windows.Media.Control;

namespace YandexMusicDiscordRpc.Services;

/// <summary>
/// Читает информацию о воспроизведении через Windows Media Session API.
/// Работает и с десктопным приложением Яндекс Музыки, и с веб-версией в браузере.
/// </summary>
public sealed class MediaSessionService : IAsyncDisposable
{
    private readonly AppLogger _logger;
    private readonly Func<AppSettings> _settingsProvider;
    private readonly BrowserWindowProbe _browserProbe;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private byte[]? _cachedArtwork;
    private string? _cachedArtworkIdentity;
    private string? _lastLoggedState;
    private bool _disposed;

    public MediaSessionService(
        AppLogger logger,
        Func<AppSettings> settingsProvider,
        BrowserWindowProbe browserProbe)
    {
        _logger = logger;
        _settingsProvider = settingsProvider;
        _browserProbe = browserProbe;
    }

    public event EventHandler<PlaybackSnapshot>? SnapshotUpdated;

    public void Start()
    {
        if (_disposed || _loopTask is not null)
            return;

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => PollLoopAsync(_cts.Token));
        _logger.Info("Служба отслеживания медиасессий запущена.");
    }

    public async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _manager = null;
        _lastLoggedState = null;
        Start();
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var cts = _cts;
            var task = _loopTask;
            _cts = null;
            _loopTask = null;

            if (cts is not null)
            {
                await cts.CancelAsync().ConfigureAwait(false);
                if (task is not null)
                {
                    try
                    {
                        await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Останавливаемся в любом случае.
                    }
                }

                cts.Dispose();
            }

            _manager = null;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await PollOnceAsync(cancellationToken).ConfigureAwait(false);
                RaiseSnapshot(snapshot);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _manager = null;
                _logger.Error("Ошибка при чтении медиасессии Windows.", ex);
                RaiseSnapshot(PlaybackSnapshot.None("Не удалось прочитать медиасессию"));
            }

            try
            {
                await Task.Delay(AppConstants.PollIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void RaiseSnapshot(PlaybackSnapshot snapshot)
    {
        try
        {
            SnapshotUpdated?.Invoke(this, snapshot);
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка обработчика снимка воспроизведения.", ex);
        }
    }

    private async Task<PlaybackSnapshot> PollOnceAsync(CancellationToken cancellationToken)
    {
        if (_manager is null)
        {
            _manager = await RequestManagerAsync(cancellationToken).ConfigureAwait(false);
            if (_manager is null)
            {
                SetState("manager-unavailable");
                return PlaybackSnapshot.None("Windows Media Session недоступен");
            }
        }

        var settings = _settingsProvider();

        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
        try
        {
            sessions = _manager.GetSessions();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Не удалось получить список медиасессий: {ex.Message}");
            _manager = null;
            return PlaybackSnapshot.None("Ожидание Windows Media Session…");
        }

        if (sessions.Count == 0)
        {
            SetState("no-sessions");
            return PlaybackSnapshot.None(
                settings.SourcePreference == SourcePreference.Web
                    ? "Откройте Яндекс Музыку в браузере"
                    : "Яндекс Музыка не запущена");
        }

        var candidates = BuildCandidates(sessions, settings.SourcePreference, out var sawYandexProcess);

        foreach (var candidate in candidates)
        {
            var track = await ReadTrackAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (track is not null)
            {
                SetState($"track:{candidate.Source}:{track.Identity}");
                return new PlaybackSnapshot
                {
                    Track = track,
                    IsYandexMusicRunning = true,
                    StatusText = candidate.Source == MusicSource.Desktop
                        ? "Яндекс Музыка • приложение"
                        : "Яндекс Музыка • веб-версия",
                    CapturedAt = track.CapturedAt
                };
            }
        }

        if (sawYandexProcess)
        {
            SetState("yandex-session-without-track");
            return new PlaybackSnapshot
            {
                IsYandexMusicRunning = true,
                StatusText = "Нет активного трека"
            };
        }

        SetState("no-yandex-session");
        return PlaybackSnapshot.None(
            settings.SourcePreference == SourcePreference.Web
                ? "Откройте Яндекс Музыку в браузере"
                : "Яндекс Музыка не запущена");
    }

    private async Task<GlobalSystemMediaTransportControlsSessionManager?> RequestManagerAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var operation = GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            return await operation.AsTask(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Не удалось подключиться к Windows Media Session: {ex.Message}");
            return null;
        }
    }

    private List<Candidate> BuildCandidates(
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions,
        SourcePreference preference,
        out bool sawYandexProcess)
    {
        sawYandexProcess = false;
        var candidates = new List<Candidate>(sessions.Count);

        foreach (var session in sessions)
        {
            string sourceId;
            try
            {
                sourceId = session.SourceAppUserModelId ?? string.Empty;
            }
            catch
            {
                continue;
            }

            var isBrowser = _browserProbe.IsBrowserSource(sourceId);
            var isDesktop = !isBrowser && LooksLikeYandex(sourceId);

            if (isBrowser &&
                (preference == SourcePreference.Web || preference == SourcePreference.Auto))
            {
                var isYandexWindow = _browserProbe.IsYandexMusicWindowOpen(sourceId);
                if (isYandexWindow)
                    sawYandexProcess = true;

                if (preference == SourcePreference.Web && !isYandexWindow)
                {
                    // В режиме «только веб» всё равно не показываем чужие сайты.
                    continue;
                }

                if (!isYandexWindow)
                    continue;

                candidates.Add(new Candidate(session, MusicSource.Web, sourceId, ScoreSession(session, isDesktop: false)));
                continue;
            }

            if (!isDesktop)
                continue;

            sawYandexProcess = true;
            if (preference == SourcePreference.Web)
                continue;

            candidates.Add(new Candidate(session, MusicSource.Desktop, sourceId, ScoreSession(session, isDesktop: true)));
        }

        candidates.Sort((left, right) => right.Score.CompareTo(left.Score));
        return candidates;
    }

    private static int ScoreSession(GlobalSystemMediaTransportControlsSession session, bool isDesktop)
    {
        // Активное воспроизведение важнее типа источника: если в браузере играет
        // Яндекс Музыка, а приложение просто стоит на паузе, выбираем браузер.
        // При равном состоянии приоритет у десктопного приложения.
        var score = isDesktop ? 10 : 5;
        try
        {
            var status = session.GetPlaybackInfo().PlaybackStatus;
            score += status switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => 100,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => 40,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => 20,
                _ => 0
            };
        }
        catch
        {
            // Если статус недоступен, сессия всё равно может содержать трек.
        }

        return score;
    }

    private async Task<TrackInfo?> ReadTrackAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            var media = await candidate.Session.TryGetMediaPropertiesAsync()
                .AsTask(timeoutCts.Token)
                .ConfigureAwait(false);

            var title = (media.Title ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(title))
                return null;

            var artist = (media.Artist ?? string.Empty).Trim();
            var album = (media.AlbumTitle ?? string.Empty).Trim();

            var playbackInfo = candidate.Session.GetPlaybackInfo();
            var playbackStatus = playbackInfo.PlaybackStatus;
            var state = playbackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackState.Playing,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackState.Paused,
                _ => PlaybackState.Stopped
            };

            if (state == PlaybackState.Stopped)
                return null;

            var timeline = candidate.Session.GetTimelineProperties();
            var duration = timeline.EndTime - timeline.StartTime;
            if (duration < TimeSpan.Zero)
                duration = TimeSpan.Zero;

            var position = timeline.Position - timeline.StartTime;
            if (position < TimeSpan.Zero)
                position = TimeSpan.Zero;

            var capturedAt = DateTimeOffset.UtcNow;
            if (state == PlaybackState.Playing)
            {
                var lastUpdated = timeline.LastUpdatedTime;
                if (lastUpdated > DateTimeOffset.UnixEpoch && lastUpdated <= capturedAt)
                {
                    var elapsed = capturedAt - lastUpdated;
                    if (elapsed < TimeSpan.FromHours(12))
                    {
                        var rate = playbackInfo.PlaybackRate ?? 1.0;
                        if (rate > 0.05 && rate < 4.0)
                            position += TimeSpan.FromMilliseconds(elapsed.TotalMilliseconds * rate);
                    }
                }
            }

            if (duration > TimeSpan.Zero && position > duration)
                position = duration;

            var identity = string.Join('\u001f', title, artist, album);
            var artwork = await GetArtworkAsync(media, identity, cancellationToken).ConfigureAwait(false);

            return new TrackInfo
            {
                Title = title,
                Artist = artist,
                Album = album,
                Duration = duration,
                Position = position,
                PlaybackState = state,
                CapturedAt = capturedAt,
                Source = candidate.Source,
                SourceAppId = candidate.SourceAppId,
                Artwork = artwork
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Не удалось прочитать данные трека: {ex.Message}");
            return null;
        }
    }

    private async Task<byte[]?> GetArtworkAsync(
        GlobalSystemMediaTransportControlsSessionMediaProperties media,
        string identity,
        CancellationToken cancellationToken)
    {
        if (string.Equals(_cachedArtworkIdentity, identity, StringComparison.Ordinal))
            return _cachedArtwork;

        _cachedArtworkIdentity = identity;
        _cachedArtwork = null;

        if (media.Thumbnail is null)
            return null;

        try
        {
            using var randomAccessStream = await media.Thumbnail.OpenReadAsync()
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            using var stream = randomAccessStream.AsStreamForRead();
            using var memory = new MemoryStream();

            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                    break;

                if (memory.Length + read > AppConstants.MaxArtworkBytes)
                {
                    _logger.Warn("Обложка трека слишком большая, пропускаем.");
                    return null;
                }

                memory.Write(buffer, 0, read);
            }

            if (memory.Length == 0)
                return null;

            _cachedArtwork = memory.ToArray();
            return _cachedArtwork;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Не удалось получить обложку из медиасессии: {ex.Message}");
            return null;
        }
    }

    private static bool LooksLikeYandex(string sourceAppId)
    {
        var id = sourceAppId.ToLowerInvariant();
        return id.Contains("yandex", StringComparison.Ordinal) ||
               id.Contains("яндекс", StringComparison.Ordinal);
    }

    private void SetState(string state)
    {
        if (string.Equals(_lastLoggedState, state, StringComparison.Ordinal))
            return;

        _lastLoggedState = state;
        _logger.Info($"Состояние медиа: {state}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _lifecycleGate.Dispose();
    }

    private sealed record Candidate(
        GlobalSystemMediaTransportControlsSession Session,
        MusicSource Source,
        string SourceAppId,
        int Score);
}

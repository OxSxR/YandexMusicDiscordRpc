using YandexMusicDiscordRpc.Models;

namespace YandexMusicDiscordRpc.Services;

/// <summary>
/// Связывает медиасессию, каталог Яндекс Музыки и Discord.
/// </summary>
public sealed class PresenceCoordinator : IAsyncDisposable
{
    private readonly MediaSessionService _media;
    private readonly DiscordPresenceService _discord;
    private readonly YandexCatalogService _catalog;
    private readonly AppLogger _logger;
    private readonly Func<AppSettings> _settingsProvider;
    private readonly object _gate = new();

    private CancellationTokenSource? _enrichmentCts;
    private TrackInfo? _currentTrack;
    private TrackMetadata? _currentMetadata;
    private string? _currentIdentity;
    private bool _enrichmentInProgress;
    private bool _enrichmentAttempted;
    private bool _started;
    private bool _disposed;

    public PresenceCoordinator(
        MediaSessionService media,
        DiscordPresenceService discord,
        YandexCatalogService catalog,
        AppLogger logger,
        Func<AppSettings> settingsProvider)
    {
        _media = media;
        _discord = discord;
        _catalog = catalog;
        _logger = logger;
        _settingsProvider = settingsProvider;
    }

    public event EventHandler<PlaybackSnapshot>? SnapshotChanged;
    public event EventHandler<TrackInfo>? TrackMetadataUpdated;

    public TrackInfo? CurrentTrack
    {
        get
        {
            lock (_gate)
                return _currentTrack;
        }
    }

    public void Start()
    {
        if (_started || _disposed)
            return;

        _started = true;
        _media.SnapshotUpdated += OnMediaSnapshot;
        _discord.Start();
        _discord.Update(null, _settingsProvider());
        _media.Start();
    }

    public void ApplySettings()
    {
        TrackInfo? track;
        lock (_gate)
            track = _currentTrack;

        _discord.Update(track, _settingsProvider());
    }

    public async Task RestartAsync()
    {
        _logger.Info("Перезапуск службы по запросу пользователя.");

        lock (_gate)
        {
            CancelEnrichmentLocked();
            _currentTrack = null;
            _currentMetadata = null;
            _currentIdentity = null;
            _enrichmentAttempted = false;
        }

        _discord.Restart();
        await _media.RestartAsync().ConfigureAwait(false);
    }

    private void OnMediaSnapshot(object? sender, PlaybackSnapshot snapshot)
    {
        var settings = _settingsProvider();
        var track = snapshot.Track;

        if (track is not null)
        {
            lock (_gate)
            {
                if (!string.Equals(_currentIdentity, track.Identity, StringComparison.Ordinal))
                {
                    _currentIdentity = track.Identity;
                    _currentMetadata = null;
                    _enrichmentAttempted = false;
                    CancelEnrichmentLocked();
                }

                if (_currentMetadata is not null)
                    track = track.WithMetadata(_currentMetadata);

                _currentTrack = track;
            }
        }
        else
        {
            lock (_gate)
            {
                _currentTrack = null;
                _currentMetadata = null;
                _currentIdentity = null;
                _enrichmentAttempted = false;
                CancelEnrichmentLocked();
            }
        }

        _discord.Update(track, settings);

        var effectiveSnapshot = snapshot with { Track = track };
        RaiseSnapshot(effectiveSnapshot);

        if (track is null || !settings.EnrichFromCatalog)
            return;

        bool shouldEnrich;
        lock (_gate)
        {
            shouldEnrich = _currentMetadata is null &&
                           !_enrichmentInProgress &&
                           !_enrichmentAttempted &&
                           _currentIdentity == track.Identity;
        }

        if (shouldEnrich)
            StartEnrichment(track);
    }

    private void StartEnrichment(TrackInfo track)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            CancelEnrichmentLocked();
            cts = new CancellationTokenSource();
            _enrichmentCts = cts;
            _enrichmentInProgress = true;
            _enrichmentAttempted = true;
        }

        _ = Task.Run(() => EnrichAsync(track, cts), CancellationToken.None);
    }

    private async Task EnrichAsync(TrackInfo track, CancellationTokenSource cts)
    {
        try
        {
            var metadata = await _catalog.FindAsync(track, cts.Token).ConfigureAwait(false);
            if (metadata is null || cts.IsCancellationRequested)
                return;

            TrackInfo enriched;
            lock (_gate)
            {
                if (!string.Equals(_currentIdentity, track.Identity, StringComparison.Ordinal))
                    return;

                _currentMetadata = metadata;
                enriched = (_currentTrack ?? track).WithMetadata(metadata);
                _currentTrack = enriched;
            }

            _logger.Info($"Каталог обогатил трек: {track.Title} — {track.Artist}.");
            _discord.Update(enriched, _settingsProvider());
            RaiseMetadataUpdated(enriched);
        }
        catch (OperationCanceledException)
        {
            // Трек сменился — это нормально.
        }
        catch (Exception ex)
        {
            _logger.Warn($"Не удалось обогатить данные трека: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_enrichmentCts, cts))
                    _enrichmentInProgress = false;
            }

            cts.Dispose();
        }
    }

    private void RaiseSnapshot(PlaybackSnapshot snapshot)
    {
        try
        {
            SnapshotChanged?.Invoke(this, snapshot);
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка обработчика снимка воспроизведения.", ex);
        }
    }

    private void RaiseMetadataUpdated(TrackInfo track)
    {
        try
        {
            TrackMetadataUpdated?.Invoke(this, track);
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка обработчика метаданных трека.", ex);
        }
    }

    private void CancelEnrichmentLocked()
    {
        var cts = _enrichmentCts;
        _enrichmentCts = null;
        _enrichmentInProgress = false;
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _media.SnapshotUpdated -= OnMediaSnapshot;
        lock (_gate)
            CancelEnrichmentLocked();

        await _media.DisposeAsync().ConfigureAwait(false);
        await _discord.DisposeAsync().ConfigureAwait(false);
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using YandexMusicDiscordRpc.Constants;
using YandexMusicDiscordRpc.Models;

namespace YandexMusicDiscordRpc.Services;

/// <summary>
/// Необязательное обогащение данных: ссылки на трек/исполнителя и публичная
/// обложка из каталога Яндекс Музыки. Без него приложение продолжает работать.
/// </summary>
public sealed class YandexCatalogService : IDisposable
{
    private readonly AppLogger _logger;
    private readonly Func<AppSettings> _settingsProvider;
    private readonly HttpClient _http;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, TrackMetadata?> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte[]> _coverCache = new(StringComparer.Ordinal);
    private bool _disposed;

    public YandexCatalogService(AppLogger logger, Func<AppSettings> settingsProvider)
    {
        _logger = logger;
        _settingsProvider = settingsProvider;

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = false,
            AllowAutoRedirect = true
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(AppConstants.YandexMusicApiBaseUrl),
            Timeout = TimeSpan.FromSeconds(8),
            MaxResponseContentBufferSize = AppConstants.MaxJsonResponseBytes
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(AppConstants.UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Yandex-Music-Client", AppConstants.YandexMusicClientHeader);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.8");
    }

    public async Task<TrackMetadata?> FindAsync(TrackInfo track, CancellationToken cancellationToken)
    {
        if (_disposed || !_settingsProvider().EnrichFromCatalog)
            return null;

        if (string.IsNullOrWhiteSpace(track.Title))
            return null;

        var key = BuildCacheKey(track);
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;
        }

        TrackMetadata? metadata = null;
        var canCache = true;
        try
        {
            var query = string.Join(' ', new[] { track.Artist, track.Title }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var url = $"/search?type=track&page=0&text={Uri.EscapeDataString(query)}";
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.Warn($"Каталог Яндекс Музыки вернул HTTP {(int)response.StatusCode} для запроса «{query}».");
                canCache = false;
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            metadata = SelectBest(document.RootElement, track);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            canCache = false;
            _logger.Warn($"Не удалось получить метаданные из каталога Яндекс Музыки: {ex.Message}");
        }

        if (canCache)
            Cache(key, metadata);

        return metadata;
    }

    public async Task<byte[]?> GetCoverBytesAsync(string? coverUrl, CancellationToken cancellationToken)
    {
        if (_disposed || string.IsNullOrWhiteSpace(coverUrl))
            return null;

        if (_coverCache.TryGetValue(coverUrl, out var cached))
            return cached;

        try
        {
            using var response = await _http.GetAsync(coverUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (data.Length == 0 || data.Length > AppConstants.MaxArtworkBytes)
                return null;

            if (_coverCache.Count > 24)
                _coverCache.Clear();
            _coverCache[coverUrl] = data;
            return data;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Не удалось загрузить обложку из каталога: {ex.Message}");
            return null;
        }
    }

    private static string BuildCacheKey(TrackInfo track)
    {
        return string.Join('\u001f',
            Normalize(track.Title),
            Normalize(track.Artist),
            Math.Round(track.Duration.TotalSeconds).ToString());
    }

    private void Cache(string key, TrackMetadata? metadata)
    {
        lock (_cacheGate)
        {
            if (_cache.Count > AppConstants.MetadataCacheEntries)
                _cache.Clear();
            _cache[key] = metadata;
        }
    }

    private static TrackMetadata? SelectBest(JsonElement root, TrackInfo track)
    {
        if (!root.TryGetProperty("result", out var result))
            return null;

        JsonElement tracksResult = default;
        var hasTracks = result.TryGetProperty("tracks", out var tracks) &&
                        tracks.TryGetProperty("results", out tracksResult) &&
                        tracksResult.ValueKind == JsonValueKind.Array;

        if (!hasTracks)
        {
            if (result.TryGetProperty("best", out var bestProperty) &&
                bestProperty.TryGetProperty("result", out var bestResult))
            {
                tracksResult = bestResult;
            }
            else
            {
                return null;
            }
        }

        var candidates = new List<(int Score, JsonElement Element)>();
        if (tracksResult.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in tracksResult.EnumerateArray())
            {
                var score = ScoreCandidate(element, track);
                if (score > 0)
                    candidates.Add((score, element));
            }
        }
        else if (tracksResult.ValueKind == JsonValueKind.Object)
        {
            var score = ScoreCandidate(tracksResult, track);
            if (score > 0)
                candidates.Add((score, tracksResult));
        }

        if (candidates.Count == 0)
            return null;

        var best = candidates.OrderByDescending(c => c.Score).First();
        if (best.Score < 5)
            return null;

        return BuildMetadata(best.Element);
    }

    private static int ScoreCandidate(JsonElement element, TrackInfo track)
    {
        var title = GetString(element, "title");
        if (string.IsNullOrWhiteSpace(title))
            return 0;

        var normalizedTitle = Normalize(title);
        var wantedTitle = Normalize(track.Title);
        var score = 0;

        if (normalizedTitle == wantedTitle)
            score += 4;
        else if (normalizedTitle.Contains(wantedTitle, StringComparison.Ordinal) ||
                 wantedTitle.Contains(normalizedTitle, StringComparison.Ordinal))
            score += 2;

        if (score == 0)
            return 0;

        var wantedArtist = Normalize(track.Artist);
        if (!string.IsNullOrWhiteSpace(wantedArtist) &&
            element.TryGetProperty("artists", out var artists) &&
            artists.ValueKind == JsonValueKind.Array)
        {
            var foundArtist = false;
            var exactArtist = false;
            foreach (var artist in artists.EnumerateArray())
            {
                var name = Normalize(GetString(artist, "name"));
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (name == wantedArtist)
                    exactArtist = true;
                else if (name.Contains(wantedArtist, StringComparison.Ordinal) ||
                         wantedArtist.Contains(name, StringComparison.Ordinal))
                    foundArtist = true;
            }

            if (exactArtist)
                score += 3;
            else if (foundArtist)
                score += 2;
        }

        var durationMs = GetLong(element, "durationMs");
        if (durationMs > 0 && track.Duration > TimeSpan.Zero)
        {
            var difference = Math.Abs(durationMs - track.Duration.TotalMilliseconds);
            if (difference <= 2500)
                score += 3;
            else if (difference <= 8000)
                score += 1;
        }

        return score;
    }

    private static TrackMetadata? BuildMetadata(JsonElement element)
    {
        var trackId = GetId(element);
        if (string.IsNullOrWhiteSpace(trackId))
            return null;

        string? albumId = null;
        string? albumTitle = null;
        string? albumCover = null;
        if (element.TryGetProperty("albums", out var albums) &&
            albums.ValueKind == JsonValueKind.Array)
        {
            foreach (var album in albums.EnumerateArray())
            {
                albumId = GetId(album);
                albumTitle = GetString(album, "title");
                albumCover = GetString(album, "coverUri");
                if (!string.IsNullOrWhiteSpace(albumId))
                    break;
            }
        }

        var artistId = string.Empty;
        if (element.TryGetProperty("artists", out var artists) &&
            artists.ValueKind == JsonValueKind.Array)
        {
            foreach (var artist in artists.EnumerateArray())
            {
                artistId = GetId(artist);
                if (!string.IsNullOrWhiteSpace(artistId))
                    break;
            }
        }

        var cover = CoverUrl(GetString(element, "coverUri") ?? albumCover);
        var trackUrl = string.IsNullOrWhiteSpace(albumId)
            ? $"{AppConstants.YandexMusicBaseUrl}/track/{trackId}"
            : $"{AppConstants.YandexMusicBaseUrl}/album/{albumId}/track/{trackId}";

        return new TrackMetadata(
            TrackUrl: trackUrl,
            ArtistUrl: string.IsNullOrWhiteSpace(artistId)
                ? null
                : $"{AppConstants.YandexMusicBaseUrl}/artist/{artistId}",
            AlbumUrl: string.IsNullOrWhiteSpace(albumId)
                ? null
                : $"{AppConstants.YandexMusicBaseUrl}/album/{albumId}",
            CoverUrl: cover,
            AlbumTitle: albumTitle);
    }

    private static string? CoverUrl(string? coverUri)
    {
        if (string.IsNullOrWhiteSpace(coverUri))
            return null;

        var normalized = coverUri.Trim();
        if (normalized.StartsWith("//", StringComparison.Ordinal))
            normalized = "https:" + normalized;
        else if (!normalized.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            normalized = "https://" + normalized;

        return normalized.Replace("%%", "1000x1000", StringComparison.Ordinal);
    }

    private static string GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return string.Empty;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            _ => string.Empty
        };
    }

    private static long GetLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var number) => number,
            _ => 0
        };
    }

    private static string GetId(JsonElement element)
    {
        if (!element.TryGetProperty("id", out var id))
            return string.Empty;

        return id.ValueKind switch
        {
            JsonValueKind.Number => id.ToString(),
            JsonValueKind.String => id.GetString() ?? string.Empty,
            _ => string.Empty
        };
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant().Replace('ё', 'е'))
        {
            if (char.IsLetterOrDigit(character))
                builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != ' ')
                builder.Append(' ');
        }

        return builder.ToString().Trim();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _http.Dispose();
        _coverCache.Clear();
        lock (_cacheGate)
        {
            _cache.Clear();
        }
    }
}

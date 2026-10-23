namespace YandexMusicDiscordRpc.Models;

public sealed record TrackInfo
{
    public required string Title { get; init; }
    public string Artist { get; init; } = string.Empty;
    public string Album { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    public TimeSpan Position { get; init; }
    public PlaybackState PlaybackState { get; init; } = PlaybackState.Unknown;
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public MusicSource Source { get; init; } = MusicSource.Unknown;
    public string SourceAppId { get; init; } = string.Empty;

    public byte[]? Artwork { get; init; }
    public string? CoverUrl { get; init; }
    public string? TrackUrl { get; init; }
    public string? ArtistUrl { get; init; }
    public string? AlbumUrl { get; init; }

    public bool IsPlaying => PlaybackState == PlaybackState.Playing;
    public bool HasArtwork => Artwork is { Length: > 0 };

    public bool HasMetadataLinks =>
        !string.IsNullOrWhiteSpace(CoverUrl) ||
        !string.IsNullOrWhiteSpace(TrackUrl) ||
        !string.IsNullOrWhiteSpace(ArtistUrl);

    /// <summary>
    /// Идентификатор трека, не зависящий от обогащения каталога.
    /// </summary>
    public string Identity =>
        string.Join('\u001f',
            Title.Trim(),
            Artist.Trim(),
            ((long)Math.Round(Duration.TotalMilliseconds)).ToString());

    public TimeSpan GetPositionAt(DateTimeOffset now)
    {
        var position = Position;
        if (IsPlaying && now > CapturedAt)
        {
            position += now - CapturedAt;
        }

        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;

        if (Duration > TimeSpan.Zero && position > Duration)
            position = Duration;

        return position;
    }

    public TrackInfo WithMetadata(TrackMetadata metadata)
    {
        return this with
        {
            CoverUrl = metadata.CoverUrl ?? CoverUrl,
            TrackUrl = metadata.TrackUrl ?? TrackUrl,
            ArtistUrl = metadata.ArtistUrl ?? ArtistUrl,
            AlbumUrl = metadata.AlbumUrl ?? AlbumUrl,
            Album = string.IsNullOrWhiteSpace(Album) ? metadata.AlbumTitle ?? Album : Album
        };
    }
}

public sealed record TrackMetadata(
    string? TrackUrl,
    string? ArtistUrl,
    string? AlbumUrl,
    string? CoverUrl,
    string? AlbumTitle);

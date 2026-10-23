namespace YandexMusicDiscordRpc.Models;

public sealed record PlaybackSnapshot
{
    public TrackInfo? Track { get; init; }
    public bool IsYandexMusicRunning { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    public static PlaybackSnapshot None(string statusText) => new()
    {
        IsYandexMusicRunning = false,
        StatusText = statusText,
        CapturedAt = DateTimeOffset.UtcNow
    };
}

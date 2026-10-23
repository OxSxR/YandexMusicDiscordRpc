namespace YandexMusicDiscordRpc.Models;

public enum SourcePreference
{
    Auto = 0,
    Web = 1,
    Desktop = 2
}

public enum StatusDisplayPreference
{
    ApplicationName = 0,
    TrackName = 1
}

public enum MusicSource
{
    Unknown = 0,
    Desktop = 1,
    Web = 2
}

public enum PlaybackState
{
    Unknown = 0,
    Playing = 1,
    Paused = 2,
    Stopped = 3
}

public enum DiscordConnectionState
{
    Disabled = 0,
    Connecting = 1,
    Connected = 2,
    Disconnected = 3,
    Error = 4
}

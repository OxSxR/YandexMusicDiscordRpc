using System.Text.Json.Serialization;
using YandexMusicDiscordRpc.Constants;

namespace YandexMusicDiscordRpc.Models;

public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    // Общие
    public bool StartWithWindows { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }

    // Discord
    public bool EnableRichPresence { get; set; } = true;
    public bool ShowCover { get; set; } = true;
    public bool ShowProgress { get; set; } = true;
    public bool ShowButtons { get; set; } = true;
    public StatusDisplayPreference StatusDisplay { get; set; } = StatusDisplayPreference.TrackName;
    public string DiscordClientId { get; set; } = AppConstants.DefaultDiscordClientId;

    // Яндекс Музыка
    public SourcePreference SourcePreference { get; set; } = SourcePreference.Auto;
    public bool EnrichFromCatalog { get; set; } = true;

    [JsonIgnore]
    public bool ShowFirstTrayHint { get; set; } = true;

    public AppSettings Clone()
    {
        return new AppSettings
        {
            Version = Version,
            StartWithWindows = StartWithWindows,
            MinimizeToTray = MinimizeToTray,
            StartMinimized = StartMinimized,
            EnableRichPresence = EnableRichPresence,
            ShowCover = ShowCover,
            ShowProgress = ShowProgress,
            ShowButtons = ShowButtons,
            StatusDisplay = StatusDisplay,
            DiscordClientId = DiscordClientId,
            SourcePreference = SourcePreference,
            EnrichFromCatalog = EnrichFromCatalog,
            ShowFirstTrayHint = ShowFirstTrayHint
        };
    }

    public void Normalize()
    {
        if (!Enum.IsDefined(SourcePreference))
            SourcePreference = SourcePreference.Auto;

        if (!Enum.IsDefined(StatusDisplay))
            StatusDisplay = StatusDisplayPreference.TrackName;

        var clientId = (DiscordClientId ?? string.Empty).Trim();
        if (!IsValidDiscordClientId(clientId))
            clientId = AppConstants.DefaultDiscordClientId;

        DiscordClientId = clientId;
        Version = 1;
    }

    public static bool IsValidDiscordClientId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (trimmed.Length is < 15 or > 22)
            return false;

        return trimmed.All(char.IsAsciiDigit);
    }
}

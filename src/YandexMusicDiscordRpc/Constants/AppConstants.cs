namespace YandexMusicDiscordRpc.Constants;

public static class AppConstants
{
    public const string AppName = "Yandex Music RPC";
    public const string DiscordApplicationName = "Яндекс Музыка";
    public const string DefaultDiscordClientId = "1560696687004225686";

    // Публичный логотип приложения Discord, используемый как запасная картинка.
    public const string DiscordAppIconUrl =
        "https://cdn.discordapp.com/app-icons/1560696687004225686/e0624fb291de1f0238282d80ef265c1d.png?size=128";

    public const string YandexMusicBaseUrl = "https://music.yandex.ru";
    public const string YandexMusicApiBaseUrl = "https://api.music.yandex.net";
    public const string YandexMusicClientHeader = "YandexMusicDesktopAppWindows/5.123.4";
    public const string UserAgent = "YandexMusicRPC/1.0 (Windows; Discord Rich Presence)";

    public const string DataFolderName = "YandexMusicDiscordRpc";
    public const string RunRegistryValueName = "YandexMusicDiscordRpc";
    public const string SingleInstanceMutexName = @"Local\YandexMusicDiscordRpc.SingleInstance";
    public const string ActivationPipeName = "YandexMusicDiscordRpc.Activate";

    public const int MaxPresenceTextLength = 128;
    public const int MaxButtonLabelBytes = 31;

    public const int PollIntervalMilliseconds = 1000;
    public const int DiscordMinimumUpdateIntervalMilliseconds = 1000;
    public const int DiscordPeriodicRefreshSeconds = 30;
    public const int DiscordConnectionResetSeconds = 20;

    public const int MaxArtworkBytes = 4 * 1024 * 1024;
    public const int MaxJsonResponseBytes = 4 * 1024 * 1024;
    public const int MetadataCacheEntries = 256;
}

namespace YandexMusicDiscordRpc.Models;

public sealed record DiscordStatus(DiscordConnectionState State, string Text)
{
    public static DiscordStatus Disabled { get; } = new(DiscordConnectionState.Disabled, "выключен");
    public static DiscordStatus Connecting { get; } = new(DiscordConnectionState.Connecting, "подключение…");
    public static DiscordStatus Disconnected { get; } = new(DiscordConnectionState.Disconnected, "не запущен");
    public static DiscordStatus Error { get; } = new(DiscordConnectionState.Error, "ошибка подключения");
}

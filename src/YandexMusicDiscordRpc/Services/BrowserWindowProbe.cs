using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace YandexMusicDiscordRpc.Services;

/// <summary>
/// Windows Media Session не сообщает URL вкладки браузера. Чтобы не принимать
/// YouTube или другой сайт за Яндекс Музыку, для веб-сессии дополнительно
/// проверяем заголовки окон соответствующего браузера.
/// </summary>
public sealed class BrowserWindowProbe
{
    private static readonly TimeSpan WindowCacheLifetime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProcessCacheLifetime = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<uint, (string Name, DateTimeOffset At)> _processNames = new();
    private List<WindowInfo> _windowCache = [];
    private DateTimeOffset _windowCacheAt = DateTimeOffset.MinValue;

    public bool IsBrowserSource(string sourceAppId)
    {
        return GetBrowserFamily(sourceAppId) is not BrowserFamily.None;
    }

    public bool IsYandexMusicWindowOpen(string sourceAppId)
    {
        var family = GetBrowserFamily(sourceAppId);
        if (family is BrowserFamily.None)
            return false;

        var processName = ProcessNameForFamily(family);
        var windows = GetWindows();
        foreach (var window in windows)
        {
            if (!string.Equals(window.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (LooksLikeYandexMusic(window.Title))
                return true;
        }

        return false;
    }

    private static bool LooksLikeYandexMusic(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var normalized = title.ToLowerInvariant().Replace('ё', 'е');
        if (!normalized.Contains("яндекс", StringComparison.Ordinal) &&
            !normalized.Contains("yandex", StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.Contains("музык", StringComparison.Ordinal) ||
               normalized.Contains("music", StringComparison.Ordinal);
    }

    private List<WindowInfo> GetWindows()
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow - _windowCacheAt < WindowCacheLifetime)
                return _windowCache;

            var windows = new List<WindowInfo>(64);
            EnumWindows((handle, _) =>
            {
                try
                {
                    if (!IsWindowVisible(handle))
                        return true;

                    var length = GetWindowTextLength(handle);
                    if (length <= 0)
                        return true;

                    var builder = new StringBuilder(length + 1);
                    if (GetWindowText(handle, builder, builder.Capacity) <= 0)
                        return true;

                    GetWindowThreadProcessId(handle, out var processId);
                    if (processId == 0)
                        return true;

                    var processName = GetProcessName(processId);
                    if (string.IsNullOrWhiteSpace(processName) || !IsBrowserProcess(processName))
                        return true;

                    windows.Add(new WindowInfo(processName, builder.ToString()));
                }
                catch
                {
                    // Одно недоступное окно не должно ломать опрос.
                }

                return true;
            }, IntPtr.Zero);

            _windowCache = windows;
            _windowCacheAt = DateTimeOffset.UtcNow;
            return windows;
        }
    }

    private string? GetProcessName(uint processId)
    {
        lock (_gate)
        {
            if (_processNames.TryGetValue(processId, out var cached) &&
                DateTimeOffset.UtcNow - cached.At < ProcessCacheLifetime)
            {
                return cached.Name;
            }
        }

        string? name = null;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            name = process.ProcessName;
        }
        catch
        {
            // Процесс уже завершился или доступ к нему закрыт.
        }

        lock (_gate)
        {
            if (_processNames.Count > 256)
                _processNames.Clear();
            _processNames[processId] = (name ?? string.Empty, DateTimeOffset.UtcNow);
        }

        return name;
    }

    private static bool IsBrowserProcess(string processName)
    {
        return processName.ToLowerInvariant() is
            "chrome" or "msedge" or "firefox" or "opera" or "opera_gx" or
            "brave" or "vivaldi" or "browser" or "chromium" or "arc";
    }

    private static BrowserFamily GetBrowserFamily(string sourceAppId)
    {
        var id = (sourceAppId ?? string.Empty).ToLowerInvariant();

        if (id.Contains("yandexbrowser", StringComparison.Ordinal) ||
            id.Contains("yandex browser", StringComparison.Ordinal) ||
            id.Contains("яндекс браузер", StringComparison.Ordinal))
        {
            return BrowserFamily.Yandex;
        }

        if (id.Contains("msedge", StringComparison.Ordinal) ||
            id.Contains("microsoft edge", StringComparison.Ordinal) ||
            id.Contains("microsoftedge", StringComparison.Ordinal))
        {
            return BrowserFamily.Edge;
        }

        if (id.Contains("brave", StringComparison.Ordinal))
            return BrowserFamily.Brave;
        if (id.Contains("vivaldi", StringComparison.Ordinal))
            return BrowserFamily.Vivaldi;
        if (id.Contains("opera", StringComparison.Ordinal))
            return BrowserFamily.Opera;
        if (id.Contains("firefox", StringComparison.Ordinal))
            return BrowserFamily.Firefox;
        if (id.Contains("chromium", StringComparison.Ordinal) || id.Contains("chrome", StringComparison.Ordinal))
            return BrowserFamily.Chrome;

        return BrowserFamily.None;
    }

    private static string ProcessNameForFamily(BrowserFamily family) => family switch
    {
        BrowserFamily.Chrome => "chrome",
        BrowserFamily.Edge => "msedge",
        BrowserFamily.Firefox => "firefox",
        BrowserFamily.Opera => "opera",
        BrowserFamily.Brave => "brave",
        BrowserFamily.Vivaldi => "vivaldi",
        BrowserFamily.Yandex => "browser",
        _ => string.Empty
    };

    private readonly record struct WindowInfo(string ProcessName, string Title);

    private enum BrowserFamily
    {
        None,
        Chrome,
        Edge,
        Firefox,
        Opera,
        Brave,
        Vivaldi,
        Yandex
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
}

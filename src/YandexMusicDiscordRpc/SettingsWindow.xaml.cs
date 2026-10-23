using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using YandexMusicDiscordRpc.Models;
using YandexMusicDiscordRpc.ViewModels;

namespace YandexMusicDiscordRpc;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel(settings);
        DataContext = _viewModel;

        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(600, Math.Max(520, workArea.Width - 24));
        Height = Math.Min(800, Math.Max(560, workArea.Height - 24));
    }

    public event EventHandler<AppSettings>? Saved;

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryCreateSettings(out var settings))
            return;

        Saved?.Invoke(this, settings);
        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Close_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (FindAncestor<System.Windows.Controls.Button>(e.OriginalSource as DependencyObject) is not null)
            return;

        try
        {
            DragMove();
        }
        catch
        {
            // Игнорируем: кнопка мыши могла быть уже отпущена.
        }
    }

    private void Hyperlink_OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Открытие браузера не критично.
        }

        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T typed)
                return typed;

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}

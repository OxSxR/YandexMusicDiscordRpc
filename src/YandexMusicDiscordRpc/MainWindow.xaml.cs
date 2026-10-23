using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using YandexMusicDiscordRpc.ViewModels;

namespace YandexMusicDiscordRpc;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(500, Math.Max(440, workArea.Width - 24));
        Height = Math.Min(720, Math.Max(560, workArea.Height - 24));

        Loaded += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.StartClock();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
            _viewModel.StartClock();
        else
            _viewModel.StopClock();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.StopClock();
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
            // DragMove может выбросить исключение, если кнопка мыши уже отпущена.
        }
    }

    private void Minimize_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Close_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
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

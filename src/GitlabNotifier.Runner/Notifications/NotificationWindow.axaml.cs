using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace GitlabNotifier.Runner.Notifications;

/// <summary>Toast-like notification at the bottom-right of the screen.</summary>
public sealed partial class NotificationWindow : Window
{
    private readonly Action? _onActivated;
    private readonly Action? _onDismissed;
    private readonly DispatcherTimer? _autoDismissTimer;

    /// <summary>
    /// Future actions share the footer width and handle their own clicks,
    /// independently of the notification's default activation.
    /// </summary>
    public ObservableCollection<Button> Actions { get; } = [];

    public NotificationWindow() : this(string.Empty, string.Empty)
    {
    }

    public NotificationWindow(string title, string body,
        TimeSpan? autoDismissAfter = null, Action? onActivated = null, Action? onDismissed = null)
    {
        InitializeComponent();
        _onActivated = onActivated;
        _onDismissed = onDismissed;
        NotificationTitle.Text = title;
        NotificationBody.Text = body;
        ActionButtons.ItemsSource = Actions;
        Actions.CollectionChanged += (_, _) => ActionButtons.IsVisible = Actions.Count > 0;

        if (autoDismissAfter is not null)
        {
            _autoDismissTimer = new DispatcherTimer { Interval = autoDismissAfter.Value };
            _autoDismissTimer.Tick += (_, _) => Close();
        }
    }

    private void PositionWindow()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            var width = (int)Math.Ceiling(Bounds.Width * scale);
            var height = (int)Math.Ceiling(Bounds.Height * scale);
            var margin = (int)Math.Ceiling(16 * scale);
            Position = new PixelPoint(area.Right - width - margin, area.Bottom - height - margin);
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        PositionWindow();
        _autoDismissTimer?.Start();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (IsVisible)
            PositionWindow();
    }

    protected override void OnClosed(EventArgs e)
    {
        _autoDismissTimer?.Stop();
        base.OnClosed(e);
    }

    private void DismissNotification(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Close();
    }

    private void DismissAndAcknowledgeNotification(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Close();
        _onDismissed?.Invoke();
    }

    private void ActivateNotification(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Close();
        _onActivated?.Invoke();
    }
}

using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitlabNotifier.Runner.Models;

namespace GitlabNotifier.Runner.Notifications;

/// <summary>Toast-like notification at the bottom-right of the screen.</summary>
public sealed partial class NotificationWindow : Window
{
    private readonly Action<MergeRequestChangeInfo>? _onActivated;
    private readonly Action? _onDismissed;
    private readonly DispatcherTimer? _autoDismissTimer;

    /// <summary>
    /// Future actions share the footer width and handle their own clicks,
    /// independently of the notification's default activation.
    /// </summary>
    public ObservableCollection<Button> Actions { get; } = [];

    public NotificationWindow() : this(string.Empty, [])
    {
    }

    public NotificationWindow(string title, IReadOnlyCollection<MergeRequestChangeInfo> mergeRequests,
        TimeSpan? autoDismissAfter = null, Action<MergeRequestChangeInfo>? onActivated = null,
        Action? onDismissed = null)
    {
        InitializeComponent();
        _onActivated = onActivated;
        _onDismissed = onDismissed;
        NotificationTitle.Text = title;
        foreach (var mergeRequest in mergeRequests)
        {
            if (MergeRequestEntries.Children.Count > 0)
            {
                MergeRequestEntries.Children.Add(new Border
                {
                    Height = 1,
                    Background = Brushes.Gray,
                    Margin = new Thickness(4, 6)
                });
            }

            var button = new Button
            {
                Classes = { "toast-flat" },
                Padding = new Thickness(4, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new TextBlock
                {
                    Text = mergeRequest.HasUnseenReviewReRequest
                        ? $"Review re-requested: {mergeRequest.Title}"
                        : $"MR needs attention: {mergeRequest.Title}",
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20
                }
            };
            AutomationProperties.SetName(button, $"Open merge request: {mergeRequest.Title}");
            button.Click += (_, e) => ActivateMergeRequest(mergeRequest, e);
            MergeRequestEntries.Children.Add(button);
        }
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

    private void DismissAndAcknowledgeNotification(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Close();
        _onDismissed?.Invoke();
    }

    private void ActivateMergeRequest(MergeRequestChangeInfo mergeRequest, RoutedEventArgs e)
    {
        e.Handled = true;
        Close();
        _onActivated?.Invoke(mergeRequest);
    }
}

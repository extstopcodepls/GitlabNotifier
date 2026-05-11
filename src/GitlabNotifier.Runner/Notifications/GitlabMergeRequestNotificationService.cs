using DesktopNotifications;
using DesktopNotifications.Windows;
using GitlabNotifier.Data;
using GitlabNotifier.Runner.Configuration;
using GitlabNotifier.Runner.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace GitlabNotifier.Runner.Notifications;

public sealed class GitlabMergeRequestNotificationService(
    IOptions<GitlabOptions> gitlabOptions,
    IOptions<RunnerOptions> runnerOptions,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<GitlabMergeRequestNotificationService> logger)
    : IHostedService, IGitlabMergeRequestNotificationService
{
    private WindowsNotificationManager? _notificationManager;
    private CancellationTokenSource? _notificationLoopCancellationTokenSource;
    private Task? _notificationLoopTask;
    private readonly SemaphoreSlim _notificationSemaphore = new(1, 1);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initializing desktop notification manager.");

        var applicationContext = WindowsApplicationContext.FromCurrentProcess(
            "GitlabNotifier.Runner",
            "GitLab Notifier");
        _notificationManager = new WindowsNotificationManager(applicationContext);
        _notificationManager.NotificationActivated += OnNotificationActivated;
        _notificationManager.NotificationDismissed += OnNotificationDismissed;

        await _notificationManager.Initialize();
        logger.LogInformation("Desktop notification manager initialized.");

        _notificationLoopCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _notificationLoopTask = RunNotificationLoopAsync(_notificationLoopCancellationTokenSource.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping desktop notification manager.");

        if (_notificationLoopCancellationTokenSource is not null)
        {
            await _notificationLoopCancellationTokenSource.CancelAsync();
        }

        if (_notificationLoopTask is not null)
        {
            try
            {
                await _notificationLoopTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_notificationManager is not null)
        {
            _notificationManager.NotificationActivated -= OnNotificationActivated;
            _notificationManager.NotificationDismissed -= OnNotificationDismissed;
            _notificationManager.Dispose();
        }
    }

    public async Task NotifyAsync(
        GitlabMergeRequestCacheUpdateResult updateResult,
        CancellationToken cancellationToken = default)
    {
        if (!updateResult.HasChanges)
        {
            return;
        }

        await ShowPendingMergeRequestNotificationAsync(cancellationToken);
    }

    private async Task RunNotificationLoopAsync(CancellationToken cancellationToken)
    {
        var reminderInterval = TimeSpan.FromSeconds(Math.Max(
            1,
            runnerOptions.Value.NotificationReminderIntervalSeconds));

        logger.LogInformation(
            "GitLab MR notification reminder loop started. Reminder interval: {ReminderInterval}.",
            reminderInterval);

        using var timer = new PeriodicTimer(reminderInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await ShowPendingMergeRequestNotificationAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("GitLab MR notification reminder loop stopped.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GitLab MR notification reminder loop failed.");
        }
    }

    private async Task ShowPendingMergeRequestNotificationAsync(CancellationToken cancellationToken)
    {
        await _notificationSemaphore.WaitAsync(cancellationToken);

        try
        {
            await ShowPendingMergeRequestNotificationCoreAsync(cancellationToken);
        }
        finally
        {
            _notificationSemaphore.Release();
        }
    }

    private async Task ShowPendingMergeRequestNotificationCoreAsync(CancellationToken cancellationToken)
    {
        if (_notificationManager is null)
        {
            logger.LogWarning("Notification manager is not initialized. Skipping desktop notification.");
            return;
        }

        var pendingMergeRequests = await GetPendingMergeRequestsAsync(cancellationToken);

        if (pendingMergeRequests.Count == 0)
        {
            logger.LogDebug("No unseen GitLab MRs pending notification.");
            return;
        }

        var body = BuildPendingNotificationBody(pendingMergeRequests);

        logger.LogInformation(
            "Showing desktop notification for unseen GitLab MRs. Count: {Count}.",
            pendingMergeRequests.Count);

        var notification = new Notification
        {
            Title = pendingMergeRequests.Count == 1
                ? "GitLab merge request changed"
                : "GitLab merge requests changed",
            Body = body
        };

        await _notificationManager.ShowNotification(notification, expirationTime: null);
    }

    private async Task<IReadOnlyCollection<MergeRequestChangeInfo>> GetPendingMergeRequestsAsync(
        CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GitlabNotifierDbContext>();

        var pendingMergeRequests = await dbContext.MergeRequests
            .AsNoTracking()
            .Where(mergeRequest => mergeRequest.HasUnseenChanges)
            .Select(mergeRequest => new PendingMergeRequestNotificationInfo
            {
                Id = mergeRequest.Id,
                Iid = mergeRequest.Iid,
                ProjectId = mergeRequest.ProjectId,
                Title = mergeRequest.Title,
                WebUrl = mergeRequest.WebUrl,
                LastSeenAt = mergeRequest.LastSeenAt,
                UnseenChangedAt = mergeRequest.UnseenChangedAt
            })
            .ToListAsync(cancellationToken);

        return pendingMergeRequests
            .OrderByDescending(mergeRequest => mergeRequest.UnseenChangedAt ?? mergeRequest.LastSeenAt)
            .Select(mergeRequest => new MergeRequestChangeInfo
            {
                Id = mergeRequest.Id,
                Iid = mergeRequest.Iid,
                ProjectId = mergeRequest.ProjectId,
                Title = mergeRequest.Title,
                WebUrl = mergeRequest.WebUrl
            })
            .ToList();
    }

    private void OnNotificationActivated(object? sender, NotificationActivatedEventArgs args)
    {
        logger.LogInformation(
            "GitLab notifier notification activated. Action: {ActionId}",
            args.ActionId);

        _ = MarkPendingMergeRequestsSeenAsync();
        OpenMergeRequestsDashboard();
    }

    private void OnNotificationDismissed(object? sender, NotificationDismissedEventArgs args)
    {
        logger.LogInformation(
            "GitLab notifier notification dismissed. Reason: {Reason}",
            args.Reason);
    }

    private async Task MarkPendingMergeRequestsSeenAsync()
    {
        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<GitlabNotifierDbContext>();
            var pendingMergeRequests = await dbContext.MergeRequests
                .Where(mergeRequest => mergeRequest.HasUnseenChanges)
                .ToListAsync();

            foreach (var mergeRequest in pendingMergeRequests)
            {
                mergeRequest.HasUnseenChanges = false;
                mergeRequest.UnseenChangedAt = null;
            }

            await dbContext.SaveChangesAsync();

            logger.LogInformation(
                "Marked unseen GitLab MRs as seen from notification click. Count: {Count}.",
                pendingMergeRequests.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark GitLab MRs as seen from notification click.");
        }
    }

    private static string BuildPendingNotificationBody(IReadOnlyCollection<MergeRequestChangeInfo> mergeRequests)
    {
        var lines = mergeRequests
            .Take(8)
            .Select(mergeRequest => $"MR needs attention: {mergeRequest.Title}")
            .ToList();

        if (mergeRequests.Count > 8)
        {
            lines.Add($"+{mergeRequests.Count - 8} more unseen MRs");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void OpenMergeRequestsDashboard()
    {
        var url = BuildMergeRequestsDashboardUrl(gitlabOptions.Value.Url);

        try
        {
            logger.LogInformation("Opening GitLab merge requests dashboard from notification click. Url: {Url}", url);
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to open GitLab merge requests dashboard: {Url}", url);
        }
    }

    private static string BuildMergeRequestsDashboardUrl(string gitlabUrl)
    {
        var baseUri = new Uri(gitlabUrl.EndsWith("/", StringComparison.Ordinal)
            ? gitlabUrl
            : $"{gitlabUrl}/");

        return new Uri(baseUri, "dashboard/merge_requests").AbsoluteUri;
    }

    private sealed class PendingMergeRequestNotificationInfo
    {
        public long Id { get; init; }

        public long Iid { get; init; }

        public long ProjectId { get; init; }

        public string Title { get; init; } = string.Empty;

        public string WebUrl { get; init; } = string.Empty;

        public DateTimeOffset LastSeenAt { get; init; }

        public DateTimeOffset? UnseenChangedAt { get; init; }
    }
}

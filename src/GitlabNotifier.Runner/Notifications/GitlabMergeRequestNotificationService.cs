using DesktopNotifications;
using DesktopNotifications.Windows;
using GitlabNotifier.Runner.Configuration;
using GitlabNotifier.Runner.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace GitlabNotifier.Runner.Notifications;

public sealed class GitlabMergeRequestNotificationService(
    IOptions<GitlabOptions> gitlabOptions,
    ILogger<GitlabMergeRequestNotificationService> logger)
    : IHostedService, IGitlabMergeRequestNotificationService
{
    private WindowsNotificationManager? _notificationManager;

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
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping desktop notification manager.");

        if (_notificationManager is not null)
        {
            _notificationManager.NotificationActivated -= OnNotificationActivated;
            _notificationManager.NotificationDismissed -= OnNotificationDismissed;
            _notificationManager.Dispose();
        }

        return Task.CompletedTask;
    }

    public async Task NotifyAsync(
        GitlabMergeRequestCacheUpdateResult updateResult,
        CancellationToken cancellationToken = default)
    {
        if (!updateResult.HasChanges)
        {
            return;
        }

        if (_notificationManager is null)
        {
            logger.LogWarning("Notification manager is not initialized. Skipping desktop notification.");
            return;
        }

        var body = BuildNotificationBody(updateResult);

        logger.LogInformation(
            "Showing desktop notification. Added: {AddedCount}; updated: {UpdatedCount}; removed: {RemovedCount}; merged removed: {MergedCount}; assigned to me: {AssignedCount}; unassigned from me: {UnassignedCount}; made reviewer: {ReviewerCount}; removed from review: {RemovedReviewerCount}; title changes: {TitleChangeCount}.",
            updateResult.AddedCount,
            updateResult.UpdatedCount,
            updateResult.RemovedCount,
            updateResult.MergedMergeRequests.Count,
            updateResult.AssignedToMeMergeRequests.Count,
            updateResult.UnassignedFromMeMergeRequests.Count,
            updateResult.ReviewerForMeMergeRequests.Count,
            updateResult.RemovedFromReviewMergeRequests.Count,
            updateResult.TitleChanges.Count);

        var notification = new Notification
        {
            Title = "GitLab merge request data changed",
            Body = body
        };

        await _notificationManager.ShowNotification(notification, expirationTime: null);
    }

    private void OnNotificationActivated(object? sender, NotificationActivatedEventArgs args)
    {
        logger.LogInformation(
            "GitLab notifier notification activated. Action: {ActionId}",
            args.ActionId);

        OpenMergeRequestsDashboard();
    }

    private void OnNotificationDismissed(object? sender, NotificationDismissedEventArgs args)
    {
        logger.LogInformation(
            "GitLab notifier notification dismissed. Reason: {Reason}",
            args.Reason);
    }

    private static string BuildNotificationBody(GitlabMergeRequestCacheUpdateResult updateResult)
    {
        var lines = new List<string>();

        AddMergeRequestLines(
            lines,
            "Assigned to you",
            updateResult.AssignedToMeMergeRequests);
        AddMergeRequestLines(
            lines,
            "Unassigned from you",
            updateResult.UnassignedFromMeMergeRequests);
        AddMergeRequestLines(
            lines,
            "Made reviewer",
            updateResult.ReviewerForMeMergeRequests);
        AddMergeRequestLines(
            lines,
            "Removed from review",
            updateResult.RemovedFromReviewMergeRequests);
        AddTitleChangeLines(lines, updateResult.TitleChanges);
        AddMergeRequestLines(
            lines,
            "Merged and removed from cache",
            updateResult.MergedMergeRequests);
        AddMergeRequestLines(
            lines,
            "New in cache",
            updateResult.AddedMergeRequests);
        AddMergeRequestLines(
            lines,
            "Removed from cache",
            updateResult.RemovedMergeRequests);

        if (updateResult.UpdatedCount > 0)
        {
            lines.Add($"Other updated MRs: {updateResult.UpdatedCount}");
        }

        return string.Join(Environment.NewLine, lines.Take(8));
    }

    private static void AddMergeRequestLines(
        ICollection<string> lines,
        string label,
        IReadOnlyCollection<MergeRequestChangeInfo> mergeRequests)
    {
        foreach (var mergeRequest in mergeRequests.Take(3))
        {
            lines.Add($"{label}: {mergeRequest.Title}");
        }

        if (mergeRequests.Count > 3)
        {
            lines.Add($"{label}: +{mergeRequests.Count - 3} more");
        }
    }

    private static void AddTitleChangeLines(
        ICollection<string> lines,
        IReadOnlyCollection<MergeRequestTitleChangeInfo> titleChanges)
    {
        foreach (var titleChange in titleChanges.Take(3))
        {
            lines.Add($"MR renamed: {titleChange.OldTitle} -> {titleChange.NewTitle}");
        }

        if (titleChanges.Count > 3)
        {
            lines.Add($"MR renamed: +{titleChanges.Count - 3} more");
        }
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
}

using Avalonia.Threading;
using GitlabNotifier.Data;
using GitlabNotifier.Runner.Configuration;
using GitlabNotifier.Runner.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GitlabNotifier.Runner.Notifications;

public sealed class GitlabMergeRequestNotificationService(
    IOptions<GitlabOptions> gitlabOptions,
    IOptions<RunnerOptions> runnerOptions,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<GitlabMergeRequestNotificationService> logger)
    : IHostedService, IGitlabMergeRequestNotificationService
{
    private CancellationTokenSource? _notificationLoopCancellationTokenSource;
    private Task? _notificationLoopTask;
    private readonly SemaphoreSlim _notificationSemaphore = new(1, 1);
    private bool _avaloniaInitialized;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initializing Avalonia notification system.");

        _avaloniaInitialized = true;
        logger.LogInformation("Avalonia notification system initialized.");

        _notificationLoopCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _notificationLoopTask = RunNotificationLoopAsync(_notificationLoopCancellationTokenSource.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping notification system.");

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
        if (!_avaloniaInitialized)
        {
            logger.LogWarning("Avalonia is not initialized. Skipping notification.");
            return;
        }

        var pendingMergeRequests = await GetPendingMergeRequestsAsync(cancellationToken);

        if (pendingMergeRequests.Count == 0)
        {
            logger.LogDebug("No unseen GitLab MRs pending notification.");
            return;
        }

        var body = BuildPendingNotificationBody(pendingMergeRequests);
        var title = pendingMergeRequests.Count == 1
            ? "GitLab merge request changed"
            : "GitLab merge requests changed";

        logger.LogInformation(
            "Showing Avalonia notification for unseen GitLab MRs. Count: {Count}.",
            pendingMergeRequests.Count);

        // Show notification on the Avalonia UI thread
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var window = new NotificationWindow(
                title: title,
                body: body,
                autoDismissAfter: TimeSpan.FromSeconds(10),
                onActivated: () =>
                {
                    _ = MarkPendingMergeRequestsSeenAsync();
                    OpenMergeRequestsDashboard();
                },
                onDismissed: () => _ = MarkPendingMergeRequestsSeenAsync());

            window.Show();
            PlayNotificationSound();
        }, DispatcherPriority.Normal, cancellationToken);
    }

    private void PlayNotificationSound()
    {
        const uint soundAsync = 0x0001;
        const uint soundAlias = 0x00010000;
        const uint soundSystem = 0x00200000;

        try
        {
            // Use the Windows notification sound scheme and System Sounds volume.
            // PlaySound falls back to the default system sound if the alias is unavailable.
            if (!PlaySound("Notification.Default", IntPtr.Zero, soundAsync | soundAlias | soundSystem))
            {
                logger.LogDebug("Windows notification sound could not be played.");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to play the Windows notification sound.");
        }
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string sound, IntPtr module, uint flags);

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

using GitlabNotifier.Data;
using GitlabNotifier.Runner.Services;
using GitlabNotifier.Runner.Configuration;
using GitlabNotifier.Runner.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GitlabNotifier.Runner.Services;

public sealed class GitlabPullerWorker(
    IServiceScopeFactory serviceScopeFactory,
    IOptions<RunnerOptions> options,
    IGitlabMergeRequestNotificationService notificationService,
    ILogger<GitlabPullerWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollInterval = TimeSpan.FromSeconds(Math.Max(1, options.Value.PollIntervalSeconds));

        logger.LogInformation("GitLab notifier runner started. Poll interval: {PollInterval}.", pollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            await PullAndUpdateCacheAsync(stoppingToken);
            await Task.Delay(pollInterval, stoppingToken);
        }
    }

    private async Task PullAndUpdateCacheAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            logger.LogInformation("Starting GitLab runner poll iteration at {StartedAt}.", startedAt);

            using var scope = serviceScopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<GitlabNotifierDbContext>();
            var gitlabDataPuller = scope.ServiceProvider.GetRequiredService<GitlabDataPuller>();
            var cacheUpdater = scope.ServiceProvider.GetRequiredService<GitlabMergeRequestCacheUpdater>();

            logger.LogInformation("Ensuring GitLab notifier database exists.");
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);

            logger.LogInformation("Pulling merge request data from GitLab.");
            var mergeRequests = await gitlabDataPuller.GetMergeRequestsForCurrentUserAsync(cancellationToken);

            logger.LogInformation(
                "Pulled merge request data. Assigned scope: {AssignedCount}; reviewer scope: {ReviewerCount}. Updating cache.",
                mergeRequests.AssignedToUser.Count,
                mergeRequests.ReviewerForUser.Count);

            var updateResult = await cacheUpdater.UpdateAsync(mergeRequests, cancellationToken);

            if (updateResult.HasChanges)
            {
                logger.LogInformation(
                    "GitLab MR cache changed. Added: {AddedCount}; updated: {UpdatedCount}; removed: {RemovedCount}; assigned to me: {AssignedCount}; unassigned from me: {UnassignedCount}; made reviewer: {ReviewerCount}; removed from review: {RemovedReviewerCount}; title changes: {TitleChangeCount}.",
                    updateResult.AddedCount,
                    updateResult.UpdatedCount,
                    updateResult.RemovedCount,
                    updateResult.AssignedToMeMergeRequests.Count,
                    updateResult.UnassignedFromMeMergeRequests.Count,
                    updateResult.ReviewerForMeMergeRequests.Count,
                    updateResult.RemovedFromReviewMergeRequests.Count,
                    updateResult.TitleChanges.Count);

                logger.LogInformation("Sending desktop notification for GitLab MR cache changes.");
                await notificationService.NotifyAsync(updateResult, cancellationToken);
            }
            else
            {
                logger.LogInformation("GitLab MR cache is already up to date.");
            }

            logger.LogInformation(
                "Finished GitLab runner poll iteration. Duration: {DurationMs} ms.",
                (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("GitLab runner poll iteration was cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GitLab puller runner iteration failed.");
        }
    }
}

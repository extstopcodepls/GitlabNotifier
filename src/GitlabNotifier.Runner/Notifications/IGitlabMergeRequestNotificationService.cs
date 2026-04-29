using GitlabNotifier.Runner.Models;

namespace GitlabNotifier.Runner.Notifications;

public interface IGitlabMergeRequestNotificationService
{
    Task NotifyAsync(
        GitlabMergeRequestCacheUpdateResult updateResult,
        CancellationToken cancellationToken = default);
}

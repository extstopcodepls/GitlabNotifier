using GitlabNotifier.Runner.Models;
using Refit;

namespace GitlabNotifier.Runner.Api;

public interface IGitlabApiClient
{
    [Get("/api/v4/merge_requests")]
    Task<ApiResponse<IReadOnlyCollection<GitlabMergeRequest>>> GetMergeRequestsAsync(
        [Query] string scope,
        [Query] string state,
        [AliasAs("per_page")] int perPage,
        [Query] int page,
        CancellationToken cancellationToken = default);

    [Get("/api/v4/projects/{projectId}/merge_requests/{mergeRequestIid}/approvals")]
    Task<ApiResponse<GitlabMergeRequestApprovals>> GetMergeRequestApprovalsAsync(
        long projectId,
        long mergeRequestIid,
        CancellationToken cancellationToken = default);

    [Get("/api/v4/projects/{projectId}/merge_requests/{mergeRequestIid}/notes?order_by=created_at&sort=desc&activity_filter=only_activity")]
    Task<ApiResponse<IReadOnlyCollection<GitlabNote>>> GetMergeRequestNotesAsync(
        long projectId,
        long mergeRequestIid,
        [AliasAs("per_page")] int perPage,
        [Query] int page,
        CancellationToken cancellationToken = default);

    [Get("/api/v4/user")]
    Task<ApiResponse<GitlabUser>> GetCurrentUserAsync(CancellationToken cancellationToken = default);
}

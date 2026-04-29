using GitlabNotifier.Runner.Api;
using GitlabNotifier.Runner.Models;
using Microsoft.Extensions.Logging;
using Refit;

namespace GitlabNotifier.Runner.Services;

public sealed class GitlabDataPuller(
    IGitlabApiClient gitlabApiClient,
    ILogger<GitlabDataPuller> logger)
{
    private const int PerPage = 100;
    private const string OpenedState = "opened";

    public async Task<GitlabMergeRequestsData> GetMergeRequestsForCurrentUserAsync(
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting GitLab merge request pull.");

        var assignedToUser = await GetMergeRequestsAsync("assigned_to_me", cancellationToken);
        var reviewerForUser = await GetMergeRequestsAsync("reviews_for_me", cancellationToken);
        var approvalsByMergeRequest = new Dictionary<(long ProjectId, long Iid), IReadOnlyCollection<MergeRequestApprovalInfo>>();

        var result = new GitlabMergeRequestsData
        {
            AssignedToUser = await AddApprovalInfoAsync(
                assignedToUser,
                approvalsByMergeRequest,
                cancellationToken),
            ReviewerForUser = await AddApprovalInfoAsync(
                reviewerForUser,
                approvalsByMergeRequest,
                cancellationToken)
        };

        logger.LogInformation(
            "Finished GitLab merge request pull. Assigned scope count: {AssignedCount}; reviewer scope count: {ReviewerCount}.",
            result.AssignedToUser.Count,
            result.ReviewerForUser.Count);

        return result;
    }

    private async Task<IReadOnlyCollection<GitlabMergeRequest>> GetMergeRequestsAsync(
        string scope,
        CancellationToken cancellationToken)
    {
        var mergeRequests = new List<GitlabMergeRequest>();
        var page = 1;

        while (true)
        {
            logger.LogDebug("Requesting GitLab merge requests. Scope: {Scope}; page: {Page}.", scope, page);

            var response = await gitlabApiClient.GetMergeRequestsAsync(
                scope,
                "all",
                PerPage,
                page,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"GitLab API request failed with status code {(int)response.StatusCode} ({response.StatusCode}).",
                    response.Error,
                    response.StatusCode);
            }

            if (response.Content is null)
            {
                throw new InvalidOperationException("GitLab API returned an empty merge request response.");
            }

            mergeRequests.AddRange(response.Content);
            logger.LogDebug(
                "Received GitLab merge request page. Scope: {Scope}; page: {Page}; page item count: {PageItemCount}; total so far: {TotalCount}.",
                scope,
                page,
                response.Content.Count,
                mergeRequests.Count);

            if (!TryGetNextPage(response, out page))
            {
                logger.LogInformation(
                    "Completed GitLab merge request list request. Scope: {Scope}; total count: {TotalCount}.",
                    scope,
                    mergeRequests.Count);

                return mergeRequests;
            }
        }
    }

    private async Task<IReadOnlyCollection<MergeRequestInfo>> AddApprovalInfoAsync(
        IReadOnlyCollection<GitlabMergeRequest> mergeRequests,
        IDictionary<(long ProjectId, long Iid), IReadOnlyCollection<MergeRequestApprovalInfo>> approvalsByMergeRequest,
        CancellationToken cancellationToken)
    {
        var result = new List<MergeRequestInfo>();

        foreach (var mergeRequest in mergeRequests)
        {
            var approvedBy = Array.Empty<MergeRequestApprovalInfo>();

            if (IsOpened(mergeRequest))
            {
                var approvalKey = (mergeRequest.ProjectId, mergeRequest.Iid);

                if (!approvalsByMergeRequest.TryGetValue(approvalKey, out var cachedApprovedBy))
                {
                    logger.LogDebug(
                        "Requesting GitLab approvals. ProjectId: {ProjectId}; MergeRequestIid: {MergeRequestIid}; Title: {Title}.",
                        mergeRequest.ProjectId,
                        mergeRequest.Iid,
                        mergeRequest.Title);

                    cachedApprovedBy = await GetApprovalsAsync(mergeRequest, cancellationToken);
                    approvalsByMergeRequest.Add(approvalKey, cachedApprovedBy);
                }

                approvedBy = cachedApprovedBy.ToArray();
            }

            result.Add(new MergeRequestInfo
            {
                Id = mergeRequest.Id,
                Iid = mergeRequest.Iid,
                ProjectId = mergeRequest.ProjectId,
                Title = mergeRequest.Title,
                WebUrl = mergeRequest.WebUrl,
                State = mergeRequest.State,
                Author = mergeRequest.Author,
                Assignees = GetAssignees(mergeRequest),
                Reviewers = mergeRequest.Reviewers ?? [],
                ApprovedBy = approvedBy
            });
        }

        return result;
    }

    private async Task<IReadOnlyCollection<MergeRequestApprovalInfo>> GetApprovalsAsync(
        GitlabMergeRequest mergeRequest,
        CancellationToken cancellationToken)
    {
        var response = await gitlabApiClient.GetMergeRequestApprovalsAsync(
            mergeRequest.ProjectId,
            mergeRequest.Iid,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GitLab approvals API request failed for project {mergeRequest.ProjectId}, merge request {mergeRequest.Iid} with status code {(int)response.StatusCode} ({response.StatusCode}).",
                response.Error,
                response.StatusCode);
        }

        if (response.Content is null)
        {
            throw new InvalidOperationException(
                $"GitLab API returned an empty approvals response for project {mergeRequest.ProjectId}, merge request {mergeRequest.Iid}.");
        }

        return (response.Content.ApprovedBy ?? [])
            .Where(approval => approval.User is not null)
            .Select(approval => new MergeRequestApprovalInfo
            {
                User = approval.User!,
                ApprovedAt = approval.ApprovedAt
            })
            .ToList();
    }

    private static bool IsOpened(GitlabMergeRequest mergeRequest)
    {
        return string.Equals(mergeRequest.State, OpenedState, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyCollection<GitlabUser> GetAssignees(GitlabMergeRequest mergeRequest)
    {
        if (mergeRequest.Assignees is { Count: > 0 })
        {
            return mergeRequest.Assignees;
        }

        return mergeRequest.Assignee is null
            ? []
            : [mergeRequest.Assignee];
    }

    private static bool TryGetNextPage<T>(ApiResponse<T> response, out int nextPage)
    {
        nextPage = 0;

        return response.Headers.TryGetValues("X-Next-Page", out var values)
            && int.TryParse(values.FirstOrDefault(), out nextPage)
            && nextPage > 0;
    }
}

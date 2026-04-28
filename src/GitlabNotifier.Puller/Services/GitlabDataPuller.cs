using GitlabNotifier.Puller.Api;
using GitlabNotifier.Puller.Models;
using Refit;

namespace GitlabNotifier.Puller.Services;

public sealed class GitlabDataPuller
{
    private const int PerPage = 100;

    private readonly IGitlabApiClient _gitlabApiClient;

    public GitlabDataPuller(IGitlabApiClient gitlabApiClient)
    {
        _gitlabApiClient = gitlabApiClient;
    }

    public async Task<GitlabMergeRequestsData> GetMergeRequestsForCurrentUserAsync(
        CancellationToken cancellationToken = default)
    {
        var assignedToUser = await GetMergeRequestsAsync("assigned_to_me", cancellationToken);
        var reviewerForUser = await GetMergeRequestsAsync("reviews_for_me", cancellationToken);
        var approvalsByMergeRequest = new Dictionary<(long ProjectId, long Iid), IReadOnlyCollection<MergeRequestApprovalInfo>>();

        return new GitlabMergeRequestsData
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
    }

    private async Task<IReadOnlyCollection<GitlabMergeRequest>> GetMergeRequestsAsync(
        string scope,
        CancellationToken cancellationToken)
    {
        var mergeRequests = new List<GitlabMergeRequest>();
        var page = 1;

        while (true)
        {
            var response = await _gitlabApiClient.GetMergeRequestsAsync(
                scope,
                "opened",
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

            if (!TryGetNextPage(response, out page))
            {
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
            var approvalKey = (mergeRequest.ProjectId, mergeRequest.Iid);

            if (!approvalsByMergeRequest.TryGetValue(approvalKey, out var approvedBy))
            {
                approvedBy = await GetApprovalsAsync(mergeRequest, cancellationToken);
                approvalsByMergeRequest.Add(approvalKey, approvedBy);
            }

            result.Add(new MergeRequestInfo
            {
                Id = mergeRequest.Id,
                Iid = mergeRequest.Iid,
                ProjectId = mergeRequest.ProjectId,
                Title = mergeRequest.Title,
                WebUrl = mergeRequest.WebUrl,
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
        var response = await _gitlabApiClient.GetMergeRequestApprovalsAsync(
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

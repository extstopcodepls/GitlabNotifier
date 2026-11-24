using GitlabNotifier.Data;
using GitlabNotifier.Runner.Api;
using GitlabNotifier.Runner.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Refit;

namespace GitlabNotifier.Runner.Services;

public sealed class GitlabDataPuller(
    IGitlabApiClient gitlabApiClient,
    GitlabNotifierDbContext dbContext,
    ILogger<GitlabDataPuller> logger)
{
    private const int PerPage = 100;
    private const string OpenedState = "opened";

    public async Task<GitlabMergeRequestsData> GetMergeRequestsForCurrentUserAsync(
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting GitLab merge request pull.");

        var assignedToUser = await GetMergeRequestsAsync("assigned_to_me", cancellationToken);
        var observedAt = DateTimeOffset.UtcNow;
        var reviewerForUser = await GetMergeRequestsAsync("reviews_for_me", cancellationToken);
        var noteScan = await TryGetReviewRequestNotesAsync(reviewerForUser, observedAt, cancellationToken);
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
                cancellationToken),
            ReviewRequestNotes = noteScan
        };

        logger.LogInformation(
            "Finished GitLab merge request pull. Assigned scope count: {AssignedCount}; reviewer scope count: {ReviewerCount}.",
            result.AssignedToUser.Count,
            result.ReviewerForUser.Count);

        return result;
    }

    private async Task<IReadOnlyCollection<ReviewRequestNoteScan>> TryGetReviewRequestNotesAsync(
        IReadOnlyCollection<GitlabMergeRequest> reviewerMergeRequests,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        if (reviewerMergeRequests.Count == 0)
        {
            return [];
        }

        GitlabUser currentUser;
        try
        {
            using var response = await gitlabApiClient.GetCurrentUserAsync(cancellationToken);
            if (!response.IsSuccessStatusCode || response.Content is not { Id: > 0 } user
                || string.IsNullOrWhiteSpace(user.Username))
            {
                logger.LogWarning("Could not identify the GitLab reviewer. Status: {Status}.", response.StatusCode);
                return [];
            }

            currentUser = user;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not identify the GitLab reviewer.");
            return [];
        }

        var mergeRequestIds = reviewerMergeRequests.Select(mergeRequest => mergeRequest.Id).ToArray();
        var cursors = await dbContext.ReviewRequestNoteBaselines
            .AsNoTracking()
            .Where(baseline => baseline.UserId == currentUser.Id && mergeRequestIds.Contains(baseline.MergeRequestId))
            .ToDictionaryAsync(baseline => baseline.MergeRequestId,
                baseline => new ReviewRequestNoteCursor(baseline.LastNoteId, baseline.IsReviewer, baseline.PendingSince),
                cancellationToken);

        var scans = new List<ReviewRequestNoteScan>();
        foreach (var mergeRequest in reviewerMergeRequests.DistinctBy(mergeRequest => mergeRequest.Id))
        {
            var cursor = cursors.GetValueOrDefault(mergeRequest.Id);
            ReviewRequestNoteScan? scan = null;
            try
            {
                scan = await ReadReviewNotesAsync(mergeRequest, currentUser, cursor, observedAt, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not read GitLab notes for {ProjectId}!{Iid}.",
                    mergeRequest.ProjectId, mergeRequest.Iid);
            }

            if (scan is not null)
            {
                scans.Add(scan);
            }
            else if (cursor is null)
            {
                // Remember when this MR was first observed even if the notes API was unavailable.
                scans.Add(new ReviewRequestNoteScan(mergeRequest.Id, currentUser, null, observedAt, null, []));
            }
        }

        return scans;
    }

    private async Task<ReviewRequestNoteScan?> ReadReviewNotesAsync(
        GitlabMergeRequest mergeRequest, GitlabUser currentUser, ReviewRequestNoteCursor? cursor,
        DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        var notes = new Dictionary<long, GitlabNote>();
        var lastNoteId = cursor?.LastNoteId ?? 0;
        var observedSince = cursor is null ? observedAt : cursor.PendingSince;
        var page = 1;
        long newestNoteId = 0;
        long[] firstPageNoteIds = [];
        var readMultiplePages = false;
        while (true)
        {
            readMultiplePages |= page > 1;
            using var response = await gitlabApiClient.GetMergeRequestNotesAsync(
                mergeRequest.ProjectId, mergeRequest.Iid, PerPage, page, cancellationToken);
            if (!response.IsSuccessStatusCode || response.Content is null)
            {
                logger.LogWarning("GitLab notes request failed for {ProjectId}!{Iid}. Status: {Status}.",
                    mergeRequest.ProjectId, mergeRequest.Iid, response.StatusCode);
                return null;
            }

            if (page == 1)
            {
                firstPageNoteIds = response.Content.Select(note => note.Id).ToArray();
                newestNoteId = firstPageNoteIds.DefaultIfEmpty().Max();
            }

            foreach (var note in response.Content)
            {
                if (note.Id > lastNoteId && (observedSince is null || note.CreatedAt >= observedSince))
                {
                    notes.TryAdd(note.Id, note);
                }
            }

            if (response.Content.Any(note => note.Id == lastNoteId
                    || (observedSince is { } since && note.CreatedAt < since))
                || !TryGetNextPage(response, out page))
            {
                break;
            }
        }

        // Offset pagination can shift while a note is added. Retry from the same cursor in that case.
        if (readMultiplePages)
        {
            using var head = await gitlabApiClient.GetMergeRequestNotesAsync(
                mergeRequest.ProjectId, mergeRequest.Iid, PerPage, 1, cancellationToken);
            if (!head.IsSuccessStatusCode || head.Content is null
                || !head.Content.Select(note => note.Id).SequenceEqual(firstPageNoteIds))
            {
                return null;
            }
        }

        return new ReviewRequestNoteScan(mergeRequest.Id, currentUser, cursor, observedAt,
            Math.Max(Math.Max(lastNoteId, newestNoteId), notes.Keys.DefaultIfEmpty().Max()), notes.Values);
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
                OpenedState,
                PerPage,
                page,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"GitLab API request failed with status code {(int)response.StatusCode.GetValueOrDefault()} ({response.StatusCode}).",
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
                $"GitLab approvals API request failed for project {mergeRequest.ProjectId}, merge request {mergeRequest.Iid} with status code {(int)response.StatusCode.GetValueOrDefault()} ({response.StatusCode}).",
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

        return response.Headers is not null
            && response.Headers.TryGetValues("X-Next-Page", out var values)
            && int.TryParse(values.FirstOrDefault(), out nextPage)
            && nextPage > 0;
    }
}

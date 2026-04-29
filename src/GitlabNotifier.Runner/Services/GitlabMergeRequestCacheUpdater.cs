using GitlabNotifier.Data;
using GitlabNotifier.Data.Entities;
using GitlabNotifier.Runner.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GitlabNotifier.Runner.Services;

public sealed class GitlabMergeRequestCacheUpdater(
    GitlabNotifierDbContext dbContext,
    ILogger<GitlabMergeRequestCacheUpdater> logger)
{
    private const string OpenedState = "opened";

    public async Task<GitlabMergeRequestCacheUpdateResult> UpdateAsync(
        GitlabMergeRequestsData mergeRequestsData,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting merge request cache update.");

        var assignedMergeRequestIds = mergeRequestsData.AssignedToUser
            .Where(IsOpened)
            .Select(mergeRequest => mergeRequest.Id)
            .ToHashSet();
        var reviewerMergeRequestIds = mergeRequestsData.ReviewerForUser
            .Where(IsOpened)
            .Select(mergeRequest => mergeRequest.Id)
            .ToHashSet();
        var mergeRequests = mergeRequestsData.AssignedToUser
            .Concat(mergeRequestsData.ReviewerForUser)
            .GroupBy(mergeRequest => mergeRequest.Id)
            .Select(group => group.First())
            .ToList();
        var currentMergeRequestIds = mergeRequests
            .Select(mergeRequest => mergeRequest.Id)
            .ToHashSet();
        var openMergeRequests = mergeRequests
            .Where(IsOpened)
            .ToList();
        var latestOpenMergeRequestIds = openMergeRequests
            .Select(mergeRequest => mergeRequest.Id)
            .ToHashSet();
        var seenAt = DateTimeOffset.UtcNow;

        logger.LogInformation(
            "Prepared cache update input. Current scoped MRs: {CurrentCount}; open MRs: {OpenCount}; assigned open: {AssignedOpenCount}; reviewer open: {ReviewerOpenCount}.",
            currentMergeRequestIds.Count,
            openMergeRequests.Count,
            assignedMergeRequestIds.Count,
            reviewerMergeRequestIds.Count);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var changeCollector = new CacheChangeCollector();

        var removedMergeRequests = await RemoveStaleMergeRequestsAsync(
            latestOpenMergeRequestIds,
            cancellationToken);
        var updatedCount = 0;

        logger.LogInformation(
            "Loaded stale cached MRs for removal. Count: {RemovedCount}.",
            removedMergeRequests.Count);

        changeCollector.RemovedMergeRequests.AddRange(removedMergeRequests
            .Where(mergeRequest => currentMergeRequestIds.Contains(mergeRequest.Id)
                || (!mergeRequest.IsAssignedToCurrentUser && !mergeRequest.IsReviewerForCurrentUser))
            .Select(ToChangeInfo));
        changeCollector.UnassignedFromMeMergeRequests.AddRange(removedMergeRequests
            .Where(mergeRequest => !currentMergeRequestIds.Contains(mergeRequest.Id)
                && mergeRequest.IsAssignedToCurrentUser)
            .Select(ToChangeInfo));
        changeCollector.RemovedFromReviewMergeRequests.AddRange(removedMergeRequests
            .Where(mergeRequest => !currentMergeRequestIds.Contains(mergeRequest.Id)
                && mergeRequest.IsReviewerForCurrentUser)
            .Select(ToChangeInfo));

        foreach (var mergeRequest in openMergeRequests)
        {
            var usersChanged = await UpsertUsersAsync(mergeRequest, cancellationToken);
            var updateResult = await UpsertMergeRequestAsync(
                mergeRequest,
                assignedMergeRequestIds.Contains(mergeRequest.Id),
                reviewerMergeRequestIds.Contains(mergeRequest.Id),
                seenAt,
                changeCollector,
                cancellationToken);

            if (updateResult == MergeRequestUpdateResult.Added)
            {
                changeCollector.AddedMergeRequests.Add(ToChangeInfo(mergeRequest));
                logger.LogInformation(
                    "MR added to cache. ProjectId: {ProjectId}; Iid: {Iid}; Title: {Title}.",
                    mergeRequest.ProjectId,
                    mergeRequest.Iid,
                    mergeRequest.Title);
            }
            else if (updateResult == MergeRequestUpdateResult.Updated || usersChanged)
            {
                updatedCount++;
                logger.LogDebug(
                    "MR updated in cache. ProjectId: {ProjectId}; Iid: {Iid}; Title: {Title}; UsersChanged: {UsersChanged}; MergeRequestChanged: {MergeRequestChanged}.",
                    mergeRequest.ProjectId,
                    mergeRequest.Iid,
                    mergeRequest.Title,
                    usersChanged,
                    updateResult == MergeRequestUpdateResult.Updated);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var result = new GitlabMergeRequestCacheUpdateResult
        {
            AddedMergeRequests = changeCollector.AddedMergeRequests,
            RemovedMergeRequests = changeCollector.RemovedMergeRequests,
            AssignedToMeMergeRequests = changeCollector.AssignedToMeMergeRequests,
            UnassignedFromMeMergeRequests = changeCollector.UnassignedFromMeMergeRequests,
            ReviewerForMeMergeRequests = changeCollector.ReviewerForMeMergeRequests,
            RemovedFromReviewMergeRequests = changeCollector.RemovedFromReviewMergeRequests,
            TitleChanges = changeCollector.TitleChanges,
            UpdatedCount = updatedCount
        };

        logger.LogInformation(
            "Finished merge request cache update. Added: {AddedCount}; updated: {UpdatedCount}; removed: {RemovedCount}; assigned to me: {AssignedCount}; unassigned from me: {UnassignedCount}; made reviewer: {ReviewerCount}; removed from review: {RemovedReviewerCount}; title changes: {TitleChangeCount}.",
            result.AddedCount,
            result.UpdatedCount,
            result.RemovedCount,
            result.AssignedToMeMergeRequests.Count,
            result.UnassignedFromMeMergeRequests.Count,
            result.ReviewerForMeMergeRequests.Count,
            result.RemovedFromReviewMergeRequests.Count,
            result.TitleChanges.Count);

        return result;
    }

    private async Task<IReadOnlyCollection<MergeRequestEntity>> RemoveStaleMergeRequestsAsync(
        IReadOnlySet<long> latestMergeRequestIds,
        CancellationToken cancellationToken)
    {
        var staleMergeRequestsQuery = dbContext.MergeRequests
            .Include(mergeRequest => mergeRequest.Assignees)
            .Include(mergeRequest => mergeRequest.Reviewers)
            .Include(mergeRequest => mergeRequest.Approvals)
            .AsSplitQuery()
            .AsQueryable();

        if (latestMergeRequestIds.Count > 0)
        {
            staleMergeRequestsQuery = staleMergeRequestsQuery
                .Where(mergeRequest => !latestMergeRequestIds.Contains(mergeRequest.Id));
        }

        var staleMergeRequests = await staleMergeRequestsQuery.ToListAsync(cancellationToken);

        foreach (var mergeRequest in staleMergeRequests)
        {
            logger.LogDebug(
                "Removing stale MR from cache. ProjectId: {ProjectId}; Iid: {Iid}; Title: {Title}; AssignedFlag: {AssignedFlag}; ReviewerFlag: {ReviewerFlag}.",
                mergeRequest.ProjectId,
                mergeRequest.Iid,
                mergeRequest.Title,
                mergeRequest.IsAssignedToCurrentUser,
                mergeRequest.IsReviewerForCurrentUser);

            dbContext.MergeRequestAssignees.RemoveRange(mergeRequest.Assignees);
            dbContext.MergeRequestReviewers.RemoveRange(mergeRequest.Reviewers);
            dbContext.MergeRequestApprovals.RemoveRange(mergeRequest.Approvals);
        }

        dbContext.MergeRequests.RemoveRange(staleMergeRequests);

        return staleMergeRequests;
    }

    private async Task<bool> UpsertUsersAsync(
        MergeRequestInfo mergeRequest,
        CancellationToken cancellationToken)
    {
        var changed = false;
        var users = mergeRequest.Assignees
            .Concat(mergeRequest.Reviewers)
            .Concat(mergeRequest.ApprovedBy.Select(approval => approval.User));

        if (mergeRequest.Author is not null)
        {
            users = users.Append(mergeRequest.Author);
        }

        foreach (var user in users.GroupBy(user => user.Id).Select(group => group.First()))
        {
            changed |= await UpsertUserAsync(user, cancellationToken);
        }

        return changed;
    }

    private async Task<bool> UpsertUserAsync(GitlabUser user, CancellationToken cancellationToken)
    {
        var entity = await dbContext.GitlabUsers.FindAsync([user.Id], cancellationToken);
        var changed = false;

        if (entity is null)
        {
            entity = new GitlabUserEntity { Id = user.Id };
            dbContext.GitlabUsers.Add(entity);
            changed = true;
            logger.LogDebug(
                "GitLab user added to cache. UserId: {UserId}; Username: {Username}; Name: {Name}.",
                user.Id,
                user.Username,
                user.Name);
        }

        if (entity.Name != user.Name)
        {
            entity.Name = user.Name;
            changed = true;
        }

        if (entity.Username != user.Username)
        {
            entity.Username = user.Username;
            changed = true;
        }

        if (entity.WebUrl != user.WebUrl)
        {
            entity.WebUrl = user.WebUrl;
            changed = true;
        }

        return changed;
    }

    private async Task<MergeRequestUpdateResult> UpsertMergeRequestAsync(
        MergeRequestInfo source,
        bool isAssignedToCurrentUser,
        bool isReviewerForCurrentUser,
        DateTimeOffset seenAt,
        CacheChangeCollector changeCollector,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.MergeRequests
            .Include(mergeRequest => mergeRequest.Assignees)
            .Include(mergeRequest => mergeRequest.Reviewers)
            .Include(mergeRequest => mergeRequest.Approvals)
            .AsSplitQuery()
            .FirstOrDefaultAsync(mergeRequest => mergeRequest.Id == source.Id, cancellationToken);

        if (entity is null)
        {
            entity = new MergeRequestEntity { Id = source.Id };
            dbContext.MergeRequests.Add(entity);
            SetMergeRequestData(entity, source, isAssignedToCurrentUser, isReviewerForCurrentUser, seenAt);
            SyncAssignees(entity, source.Assignees);
            SyncReviewers(entity, source.Reviewers);
            SyncApprovals(entity, source.ApprovedBy);

            if (isAssignedToCurrentUser)
            {
                changeCollector.AssignedToMeMergeRequests.Add(ToChangeInfo(source));
            }

            if (isReviewerForCurrentUser)
            {
                changeCollector.ReviewerForMeMergeRequests.Add(ToChangeInfo(source));
            }

            return MergeRequestUpdateResult.Added;
        }

        if (!entity.IsAssignedToCurrentUser && isAssignedToCurrentUser)
        {
            changeCollector.AssignedToMeMergeRequests.Add(ToChangeInfo(source));
            logger.LogInformation(
                "MR assigned to current user. ProjectId: {ProjectId}; Iid: {Iid}; Title: {Title}.",
                source.ProjectId,
                source.Iid,
                source.Title);
        }

        if (entity.IsAssignedToCurrentUser && !isAssignedToCurrentUser)
        {
            changeCollector.UnassignedFromMeMergeRequests.Add(ToChangeInfo(source));
            logger.LogInformation(
                "MR unassigned from current user. ProjectId: {ProjectId}; Iid: {Iid}; Title: {Title}.",
                source.ProjectId,
                source.Iid,
                source.Title);
        }

        if (!entity.IsReviewerForCurrentUser && isReviewerForCurrentUser)
        {
            changeCollector.ReviewerForMeMergeRequests.Add(ToChangeInfo(source));
            logger.LogInformation(
                "Current user added as reviewer. ProjectId: {ProjectId}; Iid: {Iid}; Title: {Title}.",
                source.ProjectId,
                source.Iid,
                source.Title);
        }

        if (entity.IsReviewerForCurrentUser && !isReviewerForCurrentUser)
        {
            changeCollector.RemovedFromReviewMergeRequests.Add(ToChangeInfo(source));
            logger.LogInformation(
                "Current user removed as reviewer. ProjectId: {ProjectId}; Iid: {Iid}; Title: {Title}.",
                source.ProjectId,
                source.Iid,
                source.Title);
        }

        if (!string.Equals(entity.Title, source.Title, StringComparison.Ordinal))
        {
            changeCollector.TitleChanges.Add(new MergeRequestTitleChangeInfo
            {
                Id = source.Id,
                Iid = source.Iid,
                ProjectId = source.ProjectId,
                OldTitle = entity.Title,
                NewTitle = source.Title,
                WebUrl = source.WebUrl
            });
            logger.LogInformation(
                "MR title changed. ProjectId: {ProjectId}; Iid: {Iid}; OldTitle: {OldTitle}; NewTitle: {NewTitle}.",
                source.ProjectId,
                source.Iid,
                entity.Title,
                source.Title);
        }

        var changed = SetMergeRequestData(
            entity,
            source,
            isAssignedToCurrentUser,
            isReviewerForCurrentUser,
            seenAt);

        changed |= SyncAssignees(entity, source.Assignees);
        changed |= SyncReviewers(entity, source.Reviewers);
        changed |= SyncApprovals(entity, source.ApprovedBy);

        return changed
            ? MergeRequestUpdateResult.Updated
            : MergeRequestUpdateResult.Unchanged;
    }

    private static bool SetMergeRequestData(
        MergeRequestEntity entity,
        MergeRequestInfo source,
        bool isAssignedToCurrentUser,
        bool isReviewerForCurrentUser,
        DateTimeOffset seenAt)
    {
        var changed = false;

        changed |= SetIfChanged(entity.Iid, source.Iid, value => entity.Iid = value);
        changed |= SetIfChanged(entity.ProjectId, source.ProjectId, value => entity.ProjectId = value);
        changed |= SetIfChanged(entity.Title, source.Title, value => entity.Title = value);
        changed |= SetIfChanged(entity.WebUrl, source.WebUrl, value => entity.WebUrl = value);
        changed |= SetIfChanged(entity.AuthorId, source.Author?.Id, value => entity.AuthorId = value);
        changed |= SetIfChanged(
            entity.IsAssignedToCurrentUser,
            isAssignedToCurrentUser,
            value => entity.IsAssignedToCurrentUser = value);
        changed |= SetIfChanged(
            entity.IsReviewerForCurrentUser,
            isReviewerForCurrentUser,
            value => entity.IsReviewerForCurrentUser = value);
        entity.LastSeenAt = seenAt;

        return changed;
    }

    private bool SyncAssignees(
        MergeRequestEntity entity,
        IReadOnlyCollection<GitlabUser> assignees)
    {
        var desiredUserIds = assignees.Select(user => user.Id).ToHashSet();
        var changed = false;

        changed |= RemoveMissingLinks(
            entity.Assignees,
            link => desiredUserIds.Contains(link.UserId));

        var existingUserIds = entity.Assignees.Select(link => link.UserId).ToHashSet();

        foreach (var userId in desiredUserIds.Except(existingUserIds))
        {
            entity.Assignees.Add(new MergeRequestAssigneeEntity
            {
                MergeRequestId = entity.Id,
                UserId = userId
            });
            changed = true;
        }

        return changed;
    }

    private bool SyncReviewers(
        MergeRequestEntity entity,
        IReadOnlyCollection<GitlabUser> reviewers)
    {
        var desiredUserIds = reviewers.Select(user => user.Id).ToHashSet();
        var changed = false;

        changed |= RemoveMissingLinks(
            entity.Reviewers,
            link => desiredUserIds.Contains(link.UserId));

        var existingUserIds = entity.Reviewers.Select(link => link.UserId).ToHashSet();

        foreach (var userId in desiredUserIds.Except(existingUserIds))
        {
            entity.Reviewers.Add(new MergeRequestReviewerEntity
            {
                MergeRequestId = entity.Id,
                UserId = userId
            });
            changed = true;
        }

        return changed;
    }

    private bool SyncApprovals(
        MergeRequestEntity entity,
        IReadOnlyCollection<MergeRequestApprovalInfo> approvals)
    {
        var desiredApprovals = approvals
            .GroupBy(approval => approval.User.Id)
            .ToDictionary(group => group.Key, group => group.First().ApprovedAt);
        var changed = false;

        changed |= RemoveMissingLinks(
            entity.Approvals,
            link => desiredApprovals.ContainsKey(link.UserId));

        foreach (var approval in entity.Approvals)
        {
            changed |= SetIfChanged(
                approval.ApprovedAt,
                desiredApprovals[approval.UserId],
                value => approval.ApprovedAt = value);
        }

        var existingUserIds = entity.Approvals.Select(link => link.UserId).ToHashSet();

        foreach (var userId in desiredApprovals.Keys.Except(existingUserIds))
        {
            entity.Approvals.Add(new MergeRequestApprovalEntity
            {
                MergeRequestId = entity.Id,
                UserId = userId,
                ApprovedAt = desiredApprovals[userId]
            });
            changed = true;
        }

        return changed;
    }

    private bool RemoveMissingLinks<TLink>(
        List<TLink> links,
        Func<TLink, bool> shouldKeep)
        where TLink : class
    {
        var changed = false;

        for (var i = links.Count - 1; i >= 0; i--)
        {
            if (shouldKeep(links[i]))
            {
                continue;
            }

            dbContext.Remove(links[i]);
            links.RemoveAt(i);
            changed = true;
        }

        return changed;
    }

    private static bool SetIfChanged<T>(T currentValue, T newValue, Action<T> setValue)
    {
        if (EqualityComparer<T>.Default.Equals(currentValue, newValue))
        {
            return false;
        }

        setValue(newValue);
        return true;
    }

    private enum MergeRequestUpdateResult
    {
        Unchanged,
        Added,
        Updated
    }

    private static bool IsOpened(MergeRequestInfo mergeRequest)
    {
        return string.Equals(mergeRequest.State, OpenedState, StringComparison.OrdinalIgnoreCase);
    }

    private static MergeRequestChangeInfo ToChangeInfo(MergeRequestInfo mergeRequest)
    {
        return new MergeRequestChangeInfo
        {
            Id = mergeRequest.Id,
            Iid = mergeRequest.Iid,
            ProjectId = mergeRequest.ProjectId,
            Title = mergeRequest.Title,
            WebUrl = mergeRequest.WebUrl
        };
    }

    private static MergeRequestChangeInfo ToChangeInfo(MergeRequestEntity mergeRequest)
    {
        return new MergeRequestChangeInfo
        {
            Id = mergeRequest.Id,
            Iid = mergeRequest.Iid,
            ProjectId = mergeRequest.ProjectId,
            Title = mergeRequest.Title,
            WebUrl = mergeRequest.WebUrl
        };
    }

    private sealed class CacheChangeCollector
    {
        public List<MergeRequestChangeInfo> AddedMergeRequests { get; } = [];

        public List<MergeRequestChangeInfo> RemovedMergeRequests { get; } = [];

        public List<MergeRequestChangeInfo> AssignedToMeMergeRequests { get; } = [];

        public List<MergeRequestChangeInfo> UnassignedFromMeMergeRequests { get; } = [];

        public List<MergeRequestChangeInfo> ReviewerForMeMergeRequests { get; } = [];

        public List<MergeRequestChangeInfo> RemovedFromReviewMergeRequests { get; } = [];

        public List<MergeRequestTitleChangeInfo> TitleChanges { get; } = [];
    }
}

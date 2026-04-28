using GitlabNotifier.Data;
using GitlabNotifier.Data.Entities;
using GitlabNotifier.Puller.Models;
using Microsoft.EntityFrameworkCore;

namespace GitlabNotifier.Puller.Services;

public sealed class GitlabMergeRequestCacheUpdater(GitlabNotifierDbContext dbContext)
{
    public async Task UpdateAsync(
        GitlabMergeRequestsData mergeRequestsData,
        CancellationToken cancellationToken = default)
    {
        var assignedMergeRequestIds = mergeRequestsData.AssignedToUser
            .Select(mergeRequest => mergeRequest.Id)
            .ToHashSet();
        var reviewerMergeRequestIds = mergeRequestsData.ReviewerForUser
            .Select(mergeRequest => mergeRequest.Id)
            .ToHashSet();
        var mergeRequests = mergeRequestsData.AssignedToUser
            .Concat(mergeRequestsData.ReviewerForUser)
            .GroupBy(mergeRequest => mergeRequest.Id)
            .Select(group => group.First())
            .ToList();
        var latestMergeRequestIds = mergeRequests
            .Select(mergeRequest => mergeRequest.Id)
            .ToHashSet();
        var seenAt = DateTimeOffset.UtcNow;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        await RemoveStaleMergeRequestsAsync(latestMergeRequestIds, cancellationToken);

        foreach (var mergeRequest in mergeRequests)
        {
            await UpsertUsersAsync(mergeRequest, cancellationToken);
            await UpsertMergeRequestAsync(
                mergeRequest,
                assignedMergeRequestIds.Contains(mergeRequest.Id),
                reviewerMergeRequestIds.Contains(mergeRequest.Id),
                seenAt,
                cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task RemoveStaleMergeRequestsAsync(
        IReadOnlySet<long> latestMergeRequestIds,
        CancellationToken cancellationToken)
    {
        var staleMergeRequestsQuery = dbContext.MergeRequests
            .Include(mergeRequest => mergeRequest.Assignees)
            .Include(mergeRequest => mergeRequest.Reviewers)
            .Include(mergeRequest => mergeRequest.Approvals)
            .AsQueryable();

        if (latestMergeRequestIds.Count > 0)
        {
            staleMergeRequestsQuery = staleMergeRequestsQuery
                .Where(mergeRequest => !latestMergeRequestIds.Contains(mergeRequest.Id));
        }

        var staleMergeRequests = await staleMergeRequestsQuery.ToListAsync(cancellationToken);

        foreach (var mergeRequest in staleMergeRequests)
        {
            dbContext.MergeRequestAssignees.RemoveRange(mergeRequest.Assignees);
            dbContext.MergeRequestReviewers.RemoveRange(mergeRequest.Reviewers);
            dbContext.MergeRequestApprovals.RemoveRange(mergeRequest.Approvals);
        }

        dbContext.MergeRequests.RemoveRange(staleMergeRequests);
    }

    private async Task UpsertUsersAsync(
        MergeRequestInfo mergeRequest,
        CancellationToken cancellationToken)
    {
        var users = mergeRequest.Assignees
            .Concat(mergeRequest.Reviewers)
            .Concat(mergeRequest.ApprovedBy.Select(approval => approval.User));

        if (mergeRequest.Author is not null)
        {
            users = users.Append(mergeRequest.Author);
        }

        foreach (var user in users.GroupBy(user => user.Id).Select(group => group.First()))
        {
            await UpsertUserAsync(user, cancellationToken);
        }
    }

    private async Task UpsertUserAsync(GitlabUser user, CancellationToken cancellationToken)
    {
        var entity = await dbContext.GitlabUsers.FindAsync([user.Id], cancellationToken);

        if (entity is null)
        {
            entity = new GitlabUserEntity { Id = user.Id };
            dbContext.GitlabUsers.Add(entity);
        }

        if (entity.Name != user.Name)
        {
            entity.Name = user.Name;
        }

        if (entity.Username != user.Username)
        {
            entity.Username = user.Username;
        }

        if (entity.WebUrl != user.WebUrl)
        {
            entity.WebUrl = user.WebUrl;
        }
    }

    private async Task UpsertMergeRequestAsync(
        MergeRequestInfo source,
        bool isAssignedToCurrentUser,
        bool isReviewerForCurrentUser,
        DateTimeOffset seenAt,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.MergeRequests
            .Include(mergeRequest => mergeRequest.Assignees)
            .Include(mergeRequest => mergeRequest.Reviewers)
            .Include(mergeRequest => mergeRequest.Approvals)
            .FirstOrDefaultAsync(mergeRequest => mergeRequest.Id == source.Id, cancellationToken);

        if (entity is null)
        {
            entity = new MergeRequestEntity { Id = source.Id };
            dbContext.MergeRequests.Add(entity);
        }

        entity.Iid = source.Iid;
        entity.ProjectId = source.ProjectId;
        entity.Title = source.Title;
        entity.WebUrl = source.WebUrl;
        entity.AuthorId = source.Author?.Id;
        entity.IsAssignedToCurrentUser = isAssignedToCurrentUser;
        entity.IsReviewerForCurrentUser = isReviewerForCurrentUser;
        entity.LastSeenAt = seenAt;

        SyncAssignees(entity, source.Assignees);
        SyncReviewers(entity, source.Reviewers);
        SyncApprovals(entity, source.ApprovedBy);
    }

    private void SyncAssignees(
        MergeRequestEntity entity,
        IReadOnlyCollection<GitlabUser> assignees)
    {
        var desiredUserIds = assignees.Select(user => user.Id).ToHashSet();

        RemoveMissingLinks(
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
        }
    }

    private void SyncReviewers(
        MergeRequestEntity entity,
        IReadOnlyCollection<GitlabUser> reviewers)
    {
        var desiredUserIds = reviewers.Select(user => user.Id).ToHashSet();

        RemoveMissingLinks(
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
        }
    }

    private void SyncApprovals(
        MergeRequestEntity entity,
        IReadOnlyCollection<MergeRequestApprovalInfo> approvals)
    {
        var desiredApprovals = approvals
            .GroupBy(approval => approval.User.Id)
            .ToDictionary(group => group.Key, group => group.First().ApprovedAt);

        RemoveMissingLinks(
            entity.Approvals,
            link => desiredApprovals.ContainsKey(link.UserId));

        foreach (var approval in entity.Approvals)
        {
            approval.ApprovedAt = desiredApprovals[approval.UserId];
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
        }
    }

    private void RemoveMissingLinks<TLink>(
        List<TLink> links,
        Func<TLink, bool> shouldKeep)
        where TLink : class
    {
        for (var i = links.Count - 1; i >= 0; i--)
        {
            if (shouldKeep(links[i]))
            {
                continue;
            }

            dbContext.Remove(links[i]);
            links.RemoveAt(i);
        }
    }
}

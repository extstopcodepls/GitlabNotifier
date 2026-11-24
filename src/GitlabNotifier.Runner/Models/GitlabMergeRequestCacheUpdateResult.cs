namespace GitlabNotifier.Runner.Models;

public sealed class GitlabMergeRequestCacheUpdateResult
{
    public IReadOnlyCollection<MergeRequestChangeInfo> AddedMergeRequests { get; init; } = [];

    public IReadOnlyCollection<MergeRequestChangeInfo> RemovedMergeRequests { get; init; } = [];

    public IReadOnlyCollection<MergeRequestChangeInfo> AssignedToMeMergeRequests { get; init; } = [];

    public IReadOnlyCollection<MergeRequestChangeInfo> UnassignedFromMeMergeRequests { get; init; } = [];

    public IReadOnlyCollection<MergeRequestChangeInfo> ReviewerForMeMergeRequests { get; init; } = [];

    public IReadOnlyCollection<MergeRequestChangeInfo> RemovedFromReviewMergeRequests { get; init; } = [];

    public IReadOnlyCollection<MergeRequestReviewRequestedInfo> ReviewRequestedOnAssignedMergeRequests { get; init; } = [];

    public IReadOnlyCollection<MergeRequestChangeInfo> ReRequestedReviewMergeRequests { get; init; } = [];

    public IReadOnlyCollection<MergeRequestTitleChangeInfo> TitleChanges { get; init; } = [];

    public int UpdatedCount { get; init; }

    public int AddedCount => AddedMergeRequests.Count;

    public int RemovedCount => RemovedMergeRequests.Count;

    public bool HasChanges =>
        AddedMergeRequests.Count > 0
        || RemovedMergeRequests.Count > 0
        || AssignedToMeMergeRequests.Count > 0
        || UnassignedFromMeMergeRequests.Count > 0
        || ReviewerForMeMergeRequests.Count > 0
        || RemovedFromReviewMergeRequests.Count > 0
        || ReviewRequestedOnAssignedMergeRequests.Count > 0
        || ReRequestedReviewMergeRequests.Count > 0
        || TitleChanges.Count > 0
        || UpdatedCount > 0;
}

public sealed class MergeRequestChangeInfo
{
    public bool HasUnseenReviewReRequest { get; init; }
    public DateTimeOffset? UnseenChangedAt { get; init; }
    public long Id { get; init; }

    public long Iid { get; init; }

    public long ProjectId { get; init; }

    public string Title { get; init; } = string.Empty;

    public string WebUrl { get; init; } = string.Empty;
}

public sealed class MergeRequestTitleChangeInfo
{
    public long Id { get; init; }

    public long Iid { get; init; }

    public long ProjectId { get; init; }

    public string OldTitle { get; init; } = string.Empty;

    public string NewTitle { get; init; } = string.Empty;

    public string WebUrl { get; init; } = string.Empty;
}

public sealed class MergeRequestReviewRequestedInfo
{
    public long Id { get; init; }

    public long Iid { get; init; }

    public long ProjectId { get; init; }

    public string Title { get; init; } = string.Empty;

    public string WebUrl { get; init; } = string.Empty;

    public IReadOnlyCollection<GitlabUser> RequestedReviewers { get; init; } = [];
}

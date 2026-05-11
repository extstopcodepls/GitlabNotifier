namespace GitlabNotifier.Data.Entities;

public sealed class MergeRequestEntity
{
    public long Id { get; set; }

    public long Iid { get; set; }

    public long ProjectId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string WebUrl { get; set; } = string.Empty;

    public long? AuthorId { get; set; }

    public GitlabUserEntity? Author { get; set; }

    public bool IsAssignedToCurrentUser { get; set; }

    public bool IsReviewerForCurrentUser { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public bool HasUnseenChanges { get; set; }

    public DateTimeOffset? UnseenChangedAt { get; set; }

    public List<MergeRequestAssigneeEntity> Assignees { get; set; } = [];

    public List<MergeRequestReviewerEntity> Reviewers { get; set; } = [];

    public List<MergeRequestApprovalEntity> Approvals { get; set; } = [];
}

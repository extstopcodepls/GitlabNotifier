namespace GitlabNotifier.Puller.Models;

public sealed class MergeRequestInfo
{
    public long Id { get; init; }

    public long Iid { get; init; }

    public long ProjectId { get; init; }

    public string Title { get; init; } = string.Empty;

    public string WebUrl { get; init; } = string.Empty;

    public GitlabUser? Author { get; init; }

    public IReadOnlyCollection<GitlabUser> Assignees { get; init; } = [];

    public IReadOnlyCollection<GitlabUser> Reviewers { get; init; } = [];

    public IReadOnlyCollection<MergeRequestApprovalInfo> ApprovedBy { get; init; } = [];
}

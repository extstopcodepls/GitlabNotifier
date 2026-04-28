namespace GitlabNotifier.Puller.Models;

public sealed class MergeRequestApprovalInfo
{
    public GitlabUser User { get; init; } = new();

    public DateTimeOffset? ApprovedAt { get; init; }
}

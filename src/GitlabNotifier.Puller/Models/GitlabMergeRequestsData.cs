namespace GitlabNotifier.Puller.Models;

public sealed class GitlabMergeRequestsData
{
    public IReadOnlyCollection<MergeRequestInfo> AssignedToUser { get; init; } = [];

    public IReadOnlyCollection<MergeRequestInfo> ReviewerForUser { get; init; } = [];
}

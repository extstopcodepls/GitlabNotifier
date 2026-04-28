namespace GitlabNotifier.Data.Entities;

public sealed class MergeRequestAssigneeEntity
{
    public long MergeRequestId { get; set; }

    public MergeRequestEntity MergeRequest { get; set; } = null!;

    public long UserId { get; set; }

    public GitlabUserEntity User { get; set; } = null!;
}

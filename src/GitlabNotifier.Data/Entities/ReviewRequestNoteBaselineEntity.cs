namespace GitlabNotifier.Data.Entities;

public sealed class ReviewRequestNoteBaselineEntity
{
    public long MergeRequestId { get; set; }

    public long UserId { get; set; }

    public long LastNoteId { get; set; }

    public bool IsReviewer { get; set; } = true;

    public DateTimeOffset? PendingSince { get; set; }
}

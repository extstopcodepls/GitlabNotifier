namespace GitlabNotifier.Runner.Models;

public sealed class GitlabMergeRequestsData
{
    public IReadOnlyCollection<MergeRequestInfo> AssignedToUser { get; init; } = [];

    public IReadOnlyCollection<MergeRequestInfo> ReviewerForUser { get; init; } = [];

    public IReadOnlyCollection<ReviewRequestNoteScan> ReviewRequestNotes { get; init; } = [];
}

public sealed record ReviewRequestNoteScan(
    long MergeRequestId,
    GitlabUser CurrentUser,
    ReviewRequestNoteCursor? PreviousCursor,
    DateTimeOffset ObservedAt,
    long? LatestNoteId,
    IReadOnlyCollection<GitlabNote> Notes);

public sealed record ReviewRequestNoteCursor(long LastNoteId, bool IsReviewer, DateTimeOffset? PendingSince);

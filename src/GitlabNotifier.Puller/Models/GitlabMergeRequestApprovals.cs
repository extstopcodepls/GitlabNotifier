using System.Text.Json.Serialization;

namespace GitlabNotifier.Puller.Models;

public sealed class GitlabMergeRequestApprovals
{
    [JsonPropertyName("approved_by")]
    public IReadOnlyCollection<GitlabMergeRequestApproval>? ApprovedBy { get; init; }
}

using System.Text.Json.Serialization;

namespace GitlabNotifier.Runner.Models;

public sealed class GitlabMergeRequestApprovals
{
    [JsonPropertyName("approved_by")]
    public IReadOnlyCollection<GitlabMergeRequestApproval>? ApprovedBy { get; init; }
}

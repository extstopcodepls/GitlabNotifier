using System.Text.Json.Serialization;

namespace GitlabNotifier.Puller.Models;

public sealed class GitlabMergeRequestApproval
{
    [JsonPropertyName("user")]
    public GitlabUser? User { get; init; }

    [JsonPropertyName("approved_at")]
    public DateTimeOffset? ApprovedAt { get; init; }
}

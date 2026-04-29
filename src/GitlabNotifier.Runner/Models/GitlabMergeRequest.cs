using System.Text.Json.Serialization;

namespace GitlabNotifier.Runner.Models;

public sealed class GitlabMergeRequest
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("iid")]
    public long Iid { get; init; }

    [JsonPropertyName("project_id")]
    public long ProjectId { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("web_url")]
    public string WebUrl { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("author")]
    public GitlabUser? Author { get; init; }

    [JsonPropertyName("assignee")]
    public GitlabUser? Assignee { get; init; }

    [JsonPropertyName("assignees")]
    public IReadOnlyCollection<GitlabUser>? Assignees { get; init; }

    [JsonPropertyName("reviewers")]
    public IReadOnlyCollection<GitlabUser>? Reviewers { get; init; }
}

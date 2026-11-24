using System.Text.Json.Serialization;

namespace GitlabNotifier.Runner.Models;

public sealed class GitlabNote
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("body")]
    public string Body { get; init; } = string.Empty;

    [JsonPropertyName("system")]
    public bool IsSystem { get; init; }

    [JsonPropertyName("author")]
    public GitlabUser? Author { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }
}

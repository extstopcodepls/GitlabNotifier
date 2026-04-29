using System.Text.Json.Serialization;

namespace GitlabNotifier.Runner.Models;

public sealed class GitlabUser
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("web_url")]
    public string WebUrl { get; init; } = string.Empty;
}

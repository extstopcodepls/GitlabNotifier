namespace GitlabNotifier.Puller.Configuration;

public sealed class GitlabOptions
{
    public string Url { get; init; } = string.Empty;

    public string ApiKey { get; init; } = string.Empty;
}

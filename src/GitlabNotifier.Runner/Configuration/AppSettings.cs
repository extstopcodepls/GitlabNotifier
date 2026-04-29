namespace GitlabNotifier.Runner.Configuration;

public sealed class AppSettings
{
    public GitlabOptions Gitlab { get; init; } = new();
}

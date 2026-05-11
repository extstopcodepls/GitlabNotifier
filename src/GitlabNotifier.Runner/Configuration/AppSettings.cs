namespace GitlabNotifier.Runner.Configuration;

public sealed class AppSettings
{
    public GitlabOptions Gitlab { get; init; } = new();

    public RunnerOptions Runner { get; init; } = new();
}

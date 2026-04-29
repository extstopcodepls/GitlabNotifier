namespace GitlabNotifier.Runner.Configuration;

public sealed class RunnerOptions
{
    public int PollIntervalSeconds { get; init; } = 300;
}

namespace GitlabNotifier.Runner.Configuration;

public sealed class RunnerOptions
{
    public int PollIntervalSeconds { get; init; } = 300;

    public int NotificationReminderIntervalSeconds { get; init; } = 300;
}

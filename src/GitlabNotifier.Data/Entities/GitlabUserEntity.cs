namespace GitlabNotifier.Data.Entities;

public sealed class GitlabUserEntity
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string WebUrl { get; set; } = string.Empty;
}

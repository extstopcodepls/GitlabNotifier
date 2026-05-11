using GitlabNotifier.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GitlabNotifier.Runner.Services;

public sealed class GitlabNotifierDatabaseInitializer(
    GitlabNotifierDbContext dbContext,
    ILogger<GitlabNotifierDatabaseInitializer> logger)
{
    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        await AddMergeRequestsColumnIfMissingAsync(
            "HasUnseenChanges",
            cancellationToken);
        await AddMergeRequestsColumnIfMissingAsync(
            "UnseenChangedAt",
            cancellationToken);
    }

    private async Task AddMergeRequestsColumnIfMissingAsync(
        string columnName,
        CancellationToken cancellationToken)
    {
        if (await MergeRequestsColumnExistsAsync(columnName, cancellationToken))
        {
            return;
        }

        var sql = columnName switch
        {
            "HasUnseenChanges" => """ALTER TABLE "MergeRequests" ADD COLUMN "HasUnseenChanges" INTEGER NOT NULL DEFAULT 0;""",
            "UnseenChangedAt" => """ALTER TABLE "MergeRequests" ADD COLUMN "UnseenChangedAt" TEXT NULL;""",
            _ => throw new InvalidOperationException($"Unsupported MergeRequests column: {columnName}.")
        };

        logger.LogInformation(
            "Adding missing MergeRequests column. Column: {ColumnName}.",
            columnName);
        await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private async Task<bool> MergeRequestsColumnExistsAsync(
        string columnName,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """PRAGMA table_info("MergeRequests");""";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

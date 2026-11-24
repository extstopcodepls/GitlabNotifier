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
        await AddColumnIfMissingAsync("MergeRequests", "HasUnseenChanges",
            """ALTER TABLE "MergeRequests" ADD COLUMN "HasUnseenChanges" INTEGER NOT NULL DEFAULT 0;""", cancellationToken);
        await AddColumnIfMissingAsync("MergeRequests", "UnseenChangedAt",
            """ALTER TABLE "MergeRequests" ADD COLUMN "UnseenChangedAt" TEXT NULL;""", cancellationToken);
        await AddColumnIfMissingAsync("MergeRequests", "HasUnseenReviewReRequest",
            """ALTER TABLE "MergeRequests" ADD COLUMN "HasUnseenReviewReRequest" INTEGER NOT NULL DEFAULT 0;""", cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "ReviewRequestNoteBaselines" (
                "MergeRequestId" INTEGER NOT NULL,
                "UserId" INTEGER NOT NULL,
                "LastNoteId" INTEGER NOT NULL,
                "IsReviewer" INTEGER NOT NULL DEFAULT 1,
                "PendingSince" TEXT NULL,
                PRIMARY KEY ("MergeRequestId", "UserId")
            );
            """,
            cancellationToken);
        await AddColumnIfMissingAsync("ReviewRequestNoteBaselines", "IsReviewer",
            """ALTER TABLE "ReviewRequestNoteBaselines" ADD COLUMN "IsReviewer" INTEGER NOT NULL DEFAULT 1;""", cancellationToken);
        await AddColumnIfMissingAsync("ReviewRequestNoteBaselines", "PendingSince",
            """ALTER TABLE "ReviewRequestNoteBaselines" ADD COLUMN "PendingSince" TEXT NULL;""", cancellationToken);
    }

    private async Task AddColumnIfMissingAsync(
        string tableName,
        string columnName,
        string sql,
        CancellationToken cancellationToken)
    {
        if (await ColumnExistsAsync(tableName, columnName, cancellationToken))
        {
            return;
        }

        logger.LogInformation(
            "Adding missing SQLite column {TableName}.{ColumnName}.",
            tableName, columnName);
        await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private async Task<bool> ColumnExistsAsync(
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """SELECT name FROM pragma_table_info($tableName);""";
        var tableParameter = command.CreateParameter();
        tableParameter.ParameterName = "$tableName";
        tableParameter.Value = tableName;
        command.Parameters.Add(tableParameter);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(0), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

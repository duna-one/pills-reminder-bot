using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace PillsReminderBot.Persistence;

public enum MigrationBaselineAction
{
    RunMigrations = 1,
    BaselineInitialMigration = 2,
    PartialLegacySchema = 3
}

public static class MigrationBootstrapper
{
    public const string InitialMigrationId = "20260526232053_InitialCreate";
    public const string InitialMigrationProductVersion = "9.0.0";

    private const string MigrationHistoryTable = "__EFMigrationsHistory";
    private const string RemindersTable = "Reminders";
    private const string UserProfilesTable = "UserProfiles";

    private const string CreateMigrationHistoryTableSql = """
        CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
            "MigrationId" character varying(150) NOT NULL,
            "ProductVersion" character varying(32) NOT NULL,
            CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
        );
        """;

    private const string InsertInitialMigrationHistorySql = $"""
        INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
        VALUES ('{InitialMigrationId}', '{InitialMigrationProductVersion}')
        ON CONFLICT ("MigrationId") DO NOTHING;
        """;

    public static MigrationBaselineAction GetBaselineAction(
        bool initialMigrationApplied,
        bool remindersTableExists,
        bool userProfilesTableExists)
    {
        if (initialMigrationApplied)
            return MigrationBaselineAction.RunMigrations;

        if (remindersTableExists && userProfilesTableExists)
            return MigrationBaselineAction.BaselineInitialMigration;

        if (!remindersTableExists && !userProfilesTableExists)
            return MigrationBaselineAction.RunMigrations;

        return MigrationBaselineAction.PartialLegacySchema;
    }

    public static async Task BaselineExistingEnsureCreatedSchemaAsync(
        AppDbContext db,
        ILogger logger,
        CancellationToken ct = default)
    {
        bool initialMigrationApplied;
        bool remindersTableExists;
        bool userProfilesTableExists;

        try
        {
            var historyTableExists = await RelationExistsAsync(db, MigrationHistoryTable, ct);
            initialMigrationApplied = historyTableExists && await MigrationHistoryContainsInitialMigrationAsync(db, ct);
            remindersTableExists = await RelationExistsAsync(db, RemindersTable, ct);
            userProfilesTableExists = await RelationExistsAsync(db, UserProfilesTable, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            logger.LogInformation("Database does not exist yet. EF migrations will create it.");
            return;
        }

        var action = GetBaselineAction(initialMigrationApplied, remindersTableExists, userProfilesTableExists);
        switch (action)
        {
            case MigrationBaselineAction.RunMigrations:
                return;

            case MigrationBaselineAction.BaselineInitialMigration:
                logger.LogInformation(
                    "Existing EnsureCreated schema detected. Baseline EF migration {MigrationId} without changing application data.",
                    InitialMigrationId);
                await db.Database.ExecuteSqlRawAsync(CreateMigrationHistoryTableSql, ct);
                await db.Database.ExecuteSqlRawAsync(InsertInitialMigrationHistorySql, ct);
                return;

            case MigrationBaselineAction.PartialLegacySchema:
                throw new InvalidOperationException(
                    "Partial legacy database schema detected: one of Reminders/UserProfiles exists without EF migration history. " +
                    "Startup stopped to avoid data loss. Back up the database and repair the schema manually before applying migrations.");

            default:
                throw new InvalidOperationException($"Unsupported migration baseline action: {action}");
        }
    }

    private static async Task<bool> RelationExistsAsync(AppDbContext db, string relationName, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "select to_regclass(@relation_name) is not null";

            var parameter = command.CreateParameter();
            parameter.ParameterName = "relation_name";
            parameter.Value = $"\"{relationName}\"";
            command.Parameters.Add(parameter);

            var result = await command.ExecuteScalarAsync(ct);
            return result is bool exists && exists;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private static async Task<bool> MigrationHistoryContainsInitialMigrationAsync(AppDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                select exists (
                    select 1
                    from "__EFMigrationsHistory"
                    where "MigrationId" = @migration_id
                )
                """;

            var parameter = command.CreateParameter();
            parameter.ParameterName = "migration_id";
            parameter.Value = InitialMigrationId;
            command.Parameters.Add(parameter);

            var result = await command.ExecuteScalarAsync(ct);
            return result is bool exists && exists;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }
}

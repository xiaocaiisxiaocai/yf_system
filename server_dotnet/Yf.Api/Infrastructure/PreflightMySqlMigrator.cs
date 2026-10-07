using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Pomelo.EntityFrameworkCore.MySql.Migrations.Internal;

namespace Yf.Api.Infrastructure;

// This provider extension point is internal API. Keep it aligned with the pinned EF/Pomelo
// versions and exercise both migration execution and script generation when upgrading them.
#pragma warning disable EF1001
/// <summary>
/// Builds every requested Down operation before executing any migration. MySQL DDL commits
/// implicitly, so encountering an unsupported Down midway cannot be undone by a transaction.
/// </summary>
internal sealed class PreflightMySqlMigrator(
    IMigrationsAssembly migrationsAssembly,
    IHistoryRepository historyRepository,
    IDatabaseCreator databaseCreator,
    IMigrationsSqlGenerator migrationsSqlGenerator,
    IRawSqlCommandBuilder rawSqlCommandBuilder,
    IMigrationCommandExecutor migrationCommandExecutor,
    IRelationalConnection connection,
    ISqlGenerationHelper sqlGenerationHelper,
    ICurrentDbContext currentContext,
    IModelRuntimeInitializer modelRuntimeInitializer,
    IDiagnosticsLogger<DbLoggerCategory.Migrations> logger,
    IRelationalCommandDiagnosticsLogger commandLogger,
    IDatabaseProvider databaseProvider,
    IMigrationsModelDiffer migrationsModelDiffer,
    IDesignTimeModel designTimeModel,
    IDbContextOptions contextOptions,
    IExecutionStrategy executionStrategy)
    : MySqlMigrator(migrationsAssembly, historyRepository, databaseCreator, migrationsSqlGenerator,
        rawSqlCommandBuilder, migrationCommandExecutor, connection, sqlGenerationHelper,
        currentContext, modelRuntimeInitializer, logger, commandLogger, databaseProvider,
        migrationsModelDiffer, designTimeModel, contextOptions, executionStrategy)
{
    protected override void PopulateMigrations(
        IEnumerable<string> appliedMigrationEntries,
        string? targetMigration,
        out MigratorData parameters)
    {
        base.PopulateMigrations(appliedMigrationEntries, targetMigration, out parameters);

        // EF calls this after acquiring its migration lock, before executing migration SQL,
        // for both Migrate and MigrateAsync. GenerateScript uses the same path without a DB.
        // DownOperations is lazy and cached: evaluating it now runs Down only to build its
        // operations; the provider will execute those same operations after preflight succeeds.
        foreach (var migration in parameters.RevertedMigrations)
        {
            try
            {
                _ = migration.DownOperations;
            }
            catch (NotSupportedException exception)
            {
                throw new NotSupportedException(
                    $"Rollback refused before executing any migration: '{migration.GetId()}' cannot be reverted. " +
                    "Restore the matching application and database backup instead. " + exception.Message,
                    exception);
            }
        }
    }
}
#pragma warning restore EF1001

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Oem.Data;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollectionDefinition.Name)]
public sealed class OemValidationMigrationTests
{
    [Fact(Timeout = 120_000)]
    public async Task UpgradePreservesFilesAndBlockedVerdictsWhileResumingPendingWork()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("oem_validation_upgrade", ct);
        await using var db = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261006015013_RestoreOemPlatform", ct);
        var now = DateTime.UtcNow;
        db.OemCompanies.Add(new OemCompany { Id = 1, Name = "Migration fixture", Status = "ACTIVE", CreatedAt = now, UpdatedAt = now });
        db.OemRetentionTemplates.Add(new OemRetentionTemplate { Id = 1, Name = "Keep", Mode = "KEEP", Status = "ACTIVE", CreatedAt = now, UpdatedAt = now });
        db.OemFlowTemplates.Add(new OemFlowTemplate { Id = 1, Name = "Approval", Status = "ACTIVE", CreatedAt = now, UpdatedAt = now });
        db.SystemConfigs.AddRange(
            new SystemConfig { CfgKey = "oem.scan.max_retries", CfgValue = "7", Description = "old retry setting" },
            new SystemConfig { CfgKey = "oem.scan.archive_max_depth", CfgValue = "3", Description = "archive depth" },
            new SystemConfig { CfgKey = "oem.validation.archive_max_depth", CfgValue = "2", Description = "explicit new value" },
            new SystemConfig { CfgKey = "oem.scan.block_on_stale_signatures", CfgValue = "true", Description = "legacy gate" },
            new SystemConfig { CfgKey = "oem.scan.max_signature_age_hours", CfgValue = "48", Description = "legacy age" },
            new SystemConfig { CfgKey = "notify.enabled", CfgValue = "false", Description = "collaboration setting" });
        await db.SaveChangesAsync(ct);
        db.OemTransfers.Add(new OemTransfer
        {
            Id = 1, Direction = "INTERNAL_TO_OEM", OemCompanyId = 1, Title = "Historical files",
            LifecycleStatus = "BLOCKED", RetentionTemplateId = 1, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        db.OemFlowInstances.Add(new OemFlowInstance
        {
            Id = 1, TransferId = 1, TemplateId = 1, Status = "WAITING_SCAN", TemplateSnapshot = "{}",
            CreatedAt = now, UpdatedAt = now,
        });
        string[] oldStatuses = ["CLEAN", "INFECTED", "UNSCANNABLE", "SCANNING", "PENDING", "ERROR"];
        for (var index = 0; index < oldStatuses.Length; index++)
        {
            var id = (ulong)index + 1;
            db.OemTransferFiles.Add(new OemTransferFile
            {
                Id = id, TransferId = 1, OriginalName = $"historical-{id}.pdf", StoredName = Guid.NewGuid().ToString(),
                Ext = "pdf", SizeBytes = 16, Sha256 = new string('a', 64), StoragePath = $"quarantine/file-{id}",
                PayloadStatus = index == 0 ? "AVAILABLE" : "QUARANTINED", ScanStatus = oldStatuses[index],
                CreatedAt = now, UpdatedAt = now,
            });
        }
        await db.SaveChangesAsync(ct);
        for (var index = 0; index < oldStatuses.Length; index++)
        {
            var id = (ulong)index + 1;
            db.OemFileScanJobs.Add(new OemFileScanJob
            {
                Id = id, FileId = id, FileSha256 = new string('a', 64), FileSizeBytes = 16,
                Status = index == 3 ? "RUNNING" : oldStatuses[index],
                AttemptCount = 3, LeaseOwner = "previous-worker", LeaseUntil = now.AddHours(1),
                NextAttemptAt = now.AddHours(1), EngineName = "historical-engine", ThreatName = index == 1 ? "historical-block" : null,
                LastError = "historical evidence", ConcurrencyVersion = 4, CreatedAt = now,
            });
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        await migrator.MigrateAsync(cancellationToken: ct);
        await migrator.MigrateAsync(cancellationToken: ct); // A repeated update must not change results or duplicate work.

        var files = await db.OemTransferFiles.OrderBy(file => file.Id).ToArrayAsync(ct);
        Assert.Equal(["VALID", "INVALID", "INVALID", "PENDING", "PENDING", "ERROR"], files.Select(file => file.ScanStatus));
        Assert.Equal(6, files.Length);
        Assert.Equal("AVAILABLE", files[0].PayloadStatus);
        Assert.All(files.Skip(1), file => Assert.Equal("QUARANTINED", file.PayloadStatus));
        Assert.All(files, file => Assert.Equal($"quarantine/file-{file.Id}", file.StoragePath));
        Assert.Equal("BLOCKED", (await db.OemTransfers.SingleAsync(ct)).LifecycleStatus);
        Assert.Equal("WAITING_FILES", (await db.OemFlowInstances.SingleAsync(ct)).Status);

        var jobs = await db.OemFileScanJobs.OrderBy(job => job.Id).ToArrayAsync(ct);
        Assert.Equal(["VALID", "INVALID", "INVALID", "PENDING", "PENDING", "ERROR"], jobs.Select(job => job.Status));
        Assert.Equal("historical-block", jobs[1].ThreatName);
        Assert.All(jobs.Skip(3).Take(2), job =>
        {
            Assert.Null(job.LeaseOwner);
            Assert.Null(job.LeaseUntil);
            Assert.Null(job.NextAttemptAt);
            Assert.Null(job.LastError);
            Assert.Equal(0, job.AttemptCount);
            Assert.Equal(5ul, job.ConcurrencyVersion);
        });
        Assert.Equal(3, jobs[5].AttemptCount);
        Assert.Equal("ERROR", jobs[5].Status);
        var settings = await db.SystemConfigs.ToDictionaryAsync(config => config.CfgKey, config => config.CfgValue, ct);
        Assert.DoesNotContain(settings.Keys, key => key.StartsWith("oem.scan.", StringComparison.Ordinal));
        Assert.Equal("7", settings["oem.validation.max_retries"]);
        Assert.Equal("2", settings["oem.validation.archive_max_depth"]);
        Assert.Equal("false", settings["notify.enabled"]);
    }
}

using System.Security.Cryptography;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Files;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

/// <summary>存储路径页面配置与在线迁移方案：启动补齐、路径校验、在线迁移、取消、冲突保留与日志目录切换。</summary>
public sealed class StorageLocationTests
{
    [Fact]
    public void TargetPathsMustBeLocalAbsoluteEmptyAndApartFromOtherLocations()
    {
        var sandbox = Sandbox();
        try
        {
            var app = Directory.CreateDirectory(Path.Combine(sandbox, "app")).FullName;
            var current = Directory.CreateDirectory(Path.Combine(sandbox, "current")).FullName;
            string Validate(string path) => StoragePathPolicy.ValidateTarget(path, app, Path.Combine(app, "wwwroot"), [current], 0);

            Assert.Equal(400, Assert.Throws<ApiException>(() => Validate("relative\\dir")).Status);
            Assert.Equal(400, Assert.Throws<ApiException>(() => Validate(@"\\server\share\yf")).Status);
            Assert.Equal(400, Assert.Throws<ApiException>(() => Validate(Path.GetPathRoot(sandbox)!)).Status);
            Assert.Equal(400, Assert.Throws<ApiException>(() => Validate(Path.Combine(app, "storage"))).Status);
            Assert.Equal(400, Assert.Throws<ApiException>(() => Validate(Path.Combine(current, "nested"))).Status);
            Assert.Equal(400, Assert.Throws<ApiException>(() => Validate(sandbox)).Status);
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (windows.Length > 0) Assert.Equal(400, Assert.Throws<ApiException>(() => Validate(Path.Combine(windows, "yf"))).Status);

            var occupied = Directory.CreateDirectory(Path.Combine(sandbox, "occupied")).FullName;
            File.WriteAllText(Path.Combine(occupied, "keep.txt"), "x");
            Assert.Equal("新目录必须为空", Assert.Throws<ApiException>(() => Validate(occupied)).Message);

            var fresh = Path.Combine(sandbox, "fresh", "storage");
            Assert.Equal(fresh, Validate(fresh));
            Assert.True(Directory.Exists(fresh));
            Assert.Empty(Directory.EnumerateFileSystemEntries(fresh));
        }
        finally { Directory.Delete(sandbox, true); }
    }

    [Fact]
    public void OemStorageReadsAndMovesExistingContentInTheRootThatHoldsIt()
    {
        var sandbox = Sandbox();
        try
        {
            var oldRoot = Directory.CreateDirectory(Path.Combine(sandbox, "oem-old")).FullName;
            var newRoot = Directory.CreateDirectory(Path.Combine(sandbox, "oem-new")).FullName;
            var options = new AppOptions { OemStorageRoot = newRoot, OemStorageFallbackRoot = oldRoot };
            var storage = new Yf.Api.Modules.Oem.Storage.OemStorage(options);
            var name = Guid.NewGuid().ToString("D");
            var quarantined = Yf.Api.Modules.Oem.Storage.OemStorage.QuarantineRelative(name);
            var oldFile = Path.Combine(oldRoot, quarantined.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(oldFile)!);
            File.WriteAllText(oldFile, "oem");

            Assert.True(storage.Migrating);
            Assert.Equal(oldFile, storage.Absolute(quarantined));
            Assert.True(storage.Exists(quarantined));
            // A promotion of old content stays in the old root, so it remains a same-volume rename.
            var available = Yf.Api.Modules.Oem.Storage.OemStorage.AvailableRelative(name);
            Assert.StartsWith(oldRoot, storage.AbsoluteBeside(available, oldFile));
            // New content goes to the primary root.
            Assert.StartsWith(newRoot, storage.Absolute(Yf.Api.Modules.Oem.Storage.OemStorage.QuarantineRelative(Guid.NewGuid().ToString("D"))));
            Assert.Equal(2, storage.AbsoluteEverywhere(quarantined).Count());
        }
        finally { Directory.Delete(sandbox, true); }
    }

    [Fact(Timeout = 120_000)]
    public async Task StartupSeedsLocationsOnceAndOnlineMigrationCopiesVerifiesAndDeletesTheOldFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = Sandbox();
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("storage_migration", ct);
        try
        {
            await database.InitializeAsync(ct);
            var (app, oldRoot, newRoot) = Roots(sandbox);
            var options = database.Options;
            options.StorageRoot = oldRoot;
            await StorageLocationBootstrap.ApplyAsync(options, app, seed: true, ct);
            options.StorageRoot = Path.Combine(sandbox, "ignored-config");
            await StorageLocationBootstrap.ApplyAsync(options, app, seed: true, ct);
            Assert.Equal(oldRoot, options.StorageRoot);
            Assert.Null(options.StorageFallbackRoot);

            var blob = await WriteAsync(oldRoot, Path.Combine("blobs", "sha256", "ab", "cd", "abcd.bin"), 300_000, ct);
            var image = await WriteAsync(oldRoot, Path.Combine("message-images", "2026", "a.png"), 1_000, ct);
            var sessionId = Guid.NewGuid().ToString("D");
            var chunk = await WriteAsync(oldRoot, Path.Combine("tmp", sessionId, "0.part"), 4_096, ct);
            await database.ExecuteAsync($"""
                SET FOREIGN_KEY_CHECKS=0;
                INSERT INTO upload_sessions(id,project_id,uploader_id,file_name,file_size,chunk_size,total_chunks,temp_dir,status,expires_at,created_at,updated_at)
                SELECT '{sessionId}',0,id,'a.bin',4096,4096,1,'{Path.Combine(oldRoot, "tmp", sessionId).Replace("\\", "\\\\")}','UPLOADING',
                       UTC_TIMESTAMP()+INTERVAL 1 HOUR,UTC_TIMESTAMP(),UTC_TIMESTAMP()
                FROM users WHERE employee_no='admin';
                SET FOREIGN_KEY_CHECKS=1;
                """, ct);

            var (service, worker, admin) = await ServicesAsync(database, app, ct);
            var started = await service.MigrateAsync(admin, "collab", new StorageMigrationRequest { Path = newRoot }, null, ct);
            Assert.Equal(("MIGRATING", "COPYING", oldRoot), (started.Status, started.Phase, started.PreviousPath));
            // New content goes to the new root at once, reads fall back to the old one.
            Assert.Equal((newRoot, oldRoot), (options.StorageRoot, options.StorageFallbackRoot));
            Assert.Equal(Path.Combine(oldRoot, "message-images", "2026", "a.png"),
                FileStorage.ResolveStoredFile(options.StorageRoot, options.StorageFallbackRoot, Path.Combine("message-images", "2026", "a.png"), ct));
            Assert.Equal(oldRoot, FileStorage.SessionRoot(options.StorageRoot, options.StorageFallbackRoot, sessionId));
            var second = await Assert.ThrowsAsync<ApiException>(() => service.MigrateAsync(
                admin, "log", new StorageMigrationRequest { Path = Path.Combine(sandbox, "logs") }, null, ct));
            Assert.Equal(409, second.Status);

            Assert.True(await worker.RunOnceAsync(ct));
            Assert.Equal("WAITING", (await ReadAsync(service, "COLLAB", ct)).Phase);
            Assert.Equal(blob, await DigestAsync(Path.Combine(newRoot, "blobs", "sha256", "ab", "cd", "abcd.bin"), ct));
            // The upload that started in the old root is still active: nothing is deleted yet.
            Assert.True(await worker.RunOnceAsync(ct));
            Assert.Equal("WAITING", (await ReadAsync(service, "COLLAB", ct)).Phase);
            Assert.True(File.Exists(Path.Combine(oldRoot, "message-images", "2026", "a.png")));

            await database.ExecuteAsync(
                $"SET FOREIGN_KEY_CHECKS=0; UPDATE upload_sessions SET status='COMPLETED' WHERE id='{sessionId}'; SET FOREIGN_KEY_CHECKS=1;", ct);
            for (var cycle = 0; cycle < 3 && (await ReadAsync(service, "COLLAB", ct)).Status != "IDLE"; cycle++)
                await worker.RunOnceAsync(ct);
            var done = await ReadAsync(service, "COLLAB", ct);
            Assert.Equal(("IDLE", null, null, newRoot), (done.Status, done.Phase, done.PreviousPath, done.Path));
            Assert.Equal((3L, 3L, 0L), (done.TotalFiles, done.DeletedFiles, done.SkippedFiles));
            Assert.Null(options.StorageFallbackRoot);
            Assert.Empty(Directory.EnumerateFiles(oldRoot, "*", SearchOption.AllDirectories));
            Assert.Equal(image, await DigestAsync(Path.Combine(newRoot, "message-images", "2026", "a.png"), ct));
            Assert.Equal(chunk, await DigestAsync(Path.Combine(newRoot, "tmp", sessionId, "0.part"), ct));
            await using var conn = await database.Database.OpenAsync(ct);
            Assert.Equal(["STORAGE_MIGRATION_START", "STORAGE_MIGRATION_COMPLETE"], (await conn.QueryAsync<string>(new CommandDefinition(
                "SELECT action FROM audit_logs WHERE action LIKE 'STORAGE_%' ORDER BY id", cancellationToken: ct))).ToArray());
        }
        finally { Directory.Delete(sandbox, true); }
    }

    [Fact(Timeout = 120_000)]
    public async Task CancellingMovesNewContentBackAndDifferingFilesAreKeptInsteadOfDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = Sandbox();
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("storage_cancel", ct);
        try
        {
            await database.InitializeAsync(ct);
            var (app, oldRoot, newRoot) = Roots(sandbox);
            var options = database.Options;
            options.StorageRoot = oldRoot;
            await StorageLocationBootstrap.ApplyAsync(options, app, seed: true, ct);
            await WriteAsync(oldRoot, Path.Combine("blobs", "a.bin"), 10_000, ct);
            var (service, worker, admin) = await ServicesAsync(database, app, ct);

            await service.MigrateAsync(admin, "COLLAB", new StorageMigrationRequest { Path = newRoot }, null, ct);
            // Written while migrating (lands in the new root), then the administrator cancels.
            var late = await WriteAsync(newRoot, Path.Combine("message-images", "late.png"), 2_000, ct);
            var cancelled = await service.CancelAsync(admin, "COLLAB", null, ct);
            Assert.Equal((oldRoot, newRoot), (cancelled.Path, cancelled.PreviousPath));
            Assert.Equal((oldRoot, newRoot), (options.StorageRoot, options.StorageFallbackRoot));
            for (var cycle = 0; cycle < 4 && (await ReadAsync(service, "COLLAB", ct)).Status != "IDLE"; cycle++)
                await worker.RunOnceAsync(ct);
            Assert.Equal("IDLE", (await ReadAsync(service, "COLLAB", ct)).Status);
            Assert.Equal(late, await DigestAsync(Path.Combine(oldRoot, "message-images", "late.png"), ct));
            Assert.Empty(Directory.EnumerateFiles(newRoot, "*", SearchOption.AllDirectories));
            Assert.Null(options.StorageFallbackRoot);

            // A target file with different content at the same relative path is never trusted: the old file stays.
            var other = Path.Combine(sandbox, "other");
            await service.MigrateAsync(admin, "COLLAB", new StorageMigrationRequest { Path = other }, null, ct);
            await WriteAsync(other, Path.Combine("blobs", "a.bin"), 10_000, ct);
            for (var cycle = 0; cycle < 4 && (await ReadAsync(service, "COLLAB", ct)).Status != "IDLE"; cycle++)
                await worker.RunOnceAsync(ct);
            var finished = await ReadAsync(service, "COLLAB", ct);
            Assert.Equal(("IDLE", 1L), (finished.Status, finished.SkippedFiles));
            Assert.Contains("旧目录保留 1 个", finished.LastError);
            Assert.True(File.Exists(Path.Combine(oldRoot, "blobs", "a.bin")));
        }
        finally { Directory.Delete(sandbox, true); }
    }

    [Fact(Timeout = 120_000)]
    public async Task LogDirectorySwitchesAtOnceAndOldLogFilesMove()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = Sandbox();
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("storage_logs", ct);
        try
        {
            await database.InitializeAsync(ct);
            var (app, oldRoot, _) = Roots(sandbox);
            var oldLogs = Directory.CreateDirectory(Path.Combine(sandbox, "logs-old")).FullName;
            var newLogs = Path.Combine(sandbox, "logs-new");
            var options = database.Options;
            options.StorageRoot = oldRoot;
            options.LogDirectory = oldLogs;
            await StorageLocationBootstrap.ApplyAsync(options, app, seed: true, ct);
            await File.WriteAllTextAsync(Path.Combine(oldLogs, "yf-api-20261030.clef"), "{}\n", ct);
            using var sink = new LogDirectorySink();
            sink.SwitchTo(oldLogs);
            var (service, worker, admin) = await ServicesAsync(database, app, ct, sink);

            await service.MigrateAsync(admin, "LOG", new StorageMigrationRequest { Path = newLogs }, null, ct);
            Assert.Equal(newLogs, sink.Directory);
            Assert.Equal(newLogs, options.LogDirectory);
            Assert.True(await worker.RunOnceAsync(ct));
            var log = await ReadAsync(service, "LOG", ct);
            Assert.True(log.Status == "IDLE", log.Status + ": " + log.LastError);
            Assert.True(File.Exists(Path.Combine(newLogs, "yf-api-20261030.clef")));
            Assert.Empty(Directory.EnumerateFiles(oldLogs));
        }
        finally { Directory.Delete(sandbox, true); }
    }

    private static async Task<(StorageLocationService Service, StorageMigrationWorker Worker, CurrentUser Admin)> ServicesAsync(
        SchemaShapeTests.SchemaDatabaseScope database, string app, CancellationToken ct, LogDirectorySink? sink = null)
    {
        var audit = new AuditService([]);
        var signal = new StorageMigrationSignal();
        var service = new StorageLocationService(database.Database, database.Options, audit, signal, new TestEnvironment(app), sink);
        var worker = new StorageMigrationWorker(database.Database, database.Options, audit, signal,
            NullLogger<StorageMigrationWorker>.Instance, sink) { DeleteGracePeriod = TimeSpan.Zero };
        await using var conn = await database.Database.OpenAsync(ct);
        // The bootstrap administrator must change its initial password before it may act.
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET must_change_password=0 WHERE employee_no='admin'", cancellationToken: ct));
        var adminId = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT id FROM users WHERE employee_no='admin'", cancellationToken: ct));
        return (service, worker, new CurrentUser(adminId, "admin", "INTERNAL", null));
    }

    private static async Task<StorageLocationResponse> ReadAsync(StorageLocationService service, string kind, CancellationToken ct) =>
        (await service.ListAsync(ct)).Items.Single(item => item.Kind == kind);

    private static string Sandbox() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "yf_storage_" + Guid.NewGuid().ToString("N"))).FullName;

    private static (string App, string OldRoot, string NewRoot) Roots(string sandbox) => (
        Directory.CreateDirectory(Path.Combine(sandbox, "app")).FullName,
        Directory.CreateDirectory(Path.Combine(sandbox, "old")).FullName,
        Path.Combine(sandbox, "new"));

    private static async Task<string> WriteAsync(string root, string relative, int size, CancellationToken ct)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = RandomNumberGenerator.GetBytes(size);
        await File.WriteAllBytesAsync(path, bytes, ct);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static async Task<string> DigestAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "Yf.Api.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Production";
    }
}

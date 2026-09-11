using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using System.Text.Json;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Files;

namespace Yf.Api.Tests;

public sealed class FilesRecoveryTests
{
    [Fact]
    public async Task SessionCommitConfirmationControlsSuccessAndDirectoryCleanup()
    {
        var unknown = await UploadService.ProbeSessionExistenceAsync(
            _ => Task.FromException<bool>(new IOException("confirmation unavailable")),
            TestContext.Current.CancellationToken);
        var exists = await UploadService.ProbeSessionExistenceAsync(
            _ => Task.FromResult(true), TestContext.Current.CancellationToken);
        var missing = await UploadService.ProbeSessionExistenceAsync(
            _ => Task.FromResult(false), TestContext.Current.CancellationToken);

        Assert.Equal(new SessionCommitRecoveryDecision(true, false),
            UploadService.SessionCommitRecovery(exists));
        Assert.Equal(new SessionCommitRecoveryDecision(false, true),
            UploadService.SessionCommitRecovery(missing));
        Assert.Equal(new SessionCommitRecoveryDecision(false, false),
            UploadService.SessionCommitRecovery(unknown));
    }

    [Fact(Timeout = 30_000)]
    public async Task GarbageCollectionDoesNotResetALiveMergeButRecoversAfterConnectionLoss()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var sessionId = Guid.NewGuid().ToString("D");
        await scope.InsertSessionAsync(sessionId, "MERGING", DateTime.UtcNow.AddHours(1));

        await using var owner = await scope.Database.OpenAsync(ct);
        await using var lease = await MySqlNamedLock.TryAcquireAsync(
            owner, UploadService.MergeLockName(owner, sessionId), 0, ct);
        Assert.NotNull(lease);

        await scope.Maintenance.RunGarbageCollectionAsync(ct);
        Assert.Equal("MERGING", await scope.StatusAsync(sessionId));

        // Simulate an IIS worker/database connection loss. Do not release the
        // named lease in application code: MySQL must release connection-owned
        // locks when the owning session disappears.
        await scope.KillConnectionAsync(owner.ServerThread, ct);
        await scope.Maintenance.RunGarbageCollectionAsync(ct);
        Assert.Equal("UPLOADING", await scope.StatusAsync(sessionId));
    }

    [Fact(Timeout = 30_000)]
    public async Task ChunkBodyIsReceivedWithoutHoldingManagementOrProjectRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var sessionId = Guid.NewGuid().ToString("D");
        await scope.InsertSessionAsync(sessionId, "UPLOADING", DateTime.UtcNow.AddHours(1));
        var body = new BlockingOneByteStream();
        var context = scope.Context(contentLength: 1);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var put = scope.Upload.PutChunkAsync(context, sessionId, 0, body, operation.Token);
        var released = false;

        try
        {
            var entered = body.WaitUntilReadAsync(operation.Token);
            var first = await Task.WhenAny(entered, put).WaitAsync(TimeSpan.FromSeconds(5), ct);
            if (first == put) await put; // Surface an early pre-read failure.
            await entered;

            using var lockProbe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lockProbe.CancelAfter(TimeSpan.FromSeconds(3));
            await using var observer = await scope.Database.OpenAsync(lockProbe.Token);
            await using var tx = await AppDb.BeginTransactionAsync(observer, lockProbe.Token);
            Assert.Equal("security.management_lock", await observer.QuerySingleAsync<string>(
                new CommandDefinition(
                    "SELECT cfg_key FROM system_configs WHERE cfg_key='security.management_lock' FOR UPDATE",
                    transaction: tx, cancellationToken: lockProbe.Token)));
            Assert.Equal(1UL, await observer.QuerySingleAsync<ulong>(new CommandDefinition(
                "SELECT id FROM projects WHERE id=1 FOR UPDATE", transaction: tx,
                cancellationToken: lockProbe.Token)));
            await tx.CommitAsync(lockProbe.Token);

            body.Release();
            released = true;
            await put.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        finally
        {
            if (!released) body.Release();
            if (!put.IsCompleted)
            {
                operation.Cancel();
                try { await put.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
                catch { }
            }
        }

        Assert.Equal([1], await File.ReadAllBytesAsync(
            FileStorage.ChunkPath(scope.StorageRoot, sessionId, 0), ct));
    }

    [Fact(Timeout = 30_000)]
    public async Task ConcurrentIdenticalMd5InitializationReturnsOneSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var request = new InitUploadRequest(1, "same.bin", 1, new string('a', 32));

        var calls = await Task.WhenAll(
            scope.Upload.InitAsync(scope.Context(), request, ct),
            scope.Upload.InitAsync(scope.Context(), request, ct));
        var ids = calls.Select(result =>
            JsonSerializer.SerializeToElement(result).GetProperty("sessionId").GetString()).ToArray();

        Assert.NotNull(ids[0]);
        Assert.Equal(ids[0], ids[1]);
        await using var connection = await scope.Database.OpenAsync(ct);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM upload_sessions
            WHERE project_id=1 AND uploader_id=1 AND file_name='same.bin'
              AND file_size=1 AND file_md5=@Md5
            """, new { Md5 = new string('a', 32) }, cancellationToken: ct)));
    }

    [Fact(Timeout = 30_000)]
    public async Task Md5LessAbandonedMergeCanBeRetriedAndThenAborted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var sessionId = Guid.NewGuid().ToString("D");
        await scope.InsertSessionAsync(sessionId, "MERGING", DateTime.UtcNow.AddHours(1));

        var incomplete = await Assert.ThrowsAsync<ApiException>(() =>
            scope.Upload.MergeAsync(scope.Context(), sessionId, ct));
        Assert.Equal(400, incomplete.Status);
        Assert.Equal("MERGING", await scope.StatusAsync(sessionId));

        await scope.Upload.AbortAsync(scope.Context(), sessionId, ct);
        Assert.Equal("ABORTED", await scope.StatusAsync(sessionId));
        Assert.False(Directory.Exists(FileStorage.SessionDirectory(scope.StorageRoot, sessionId)));
    }

    [Fact(Timeout = 30_000)]
    public async Task ExpiredAbandonedMergeRemovesOnlyUnreferencedPendingFinals()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var sessionId = Guid.NewGuid().ToString("D");
        await scope.InsertSessionAsync(sessionId, "MERGING", DateTime.UtcNow.AddMinutes(-1));

        var sessionDirectory = FileStorage.SessionDirectory(scope.StorageRoot, sessionId);
        Directory.CreateDirectory(sessionDirectory);
        var referenced = scope.CreateFinal($"{Guid.NewGuid():D}.bin");
        var orphan = scope.CreateFinal($"{Guid.NewGuid():D}.bin");
        var unrelated = scope.CreateFinal($"{Guid.NewGuid():D}.bin");
        var outside = Path.Combine(Path.GetDirectoryName(scope.StorageRoot)!, "outside.bin");
        await File.WriteAllBytesAsync(outside, [2], ct);
        await scope.ReferenceFileAsync(referenced);
        await WriteMarkerAsync(sessionDirectory, referenced);
        await WriteMarkerAsync(sessionDirectory, orphan);
        await File.WriteAllBytesAsync(
            Path.Combine(sessionDirectory, UploadService.PendingFinalMarkerPrefix + Guid.NewGuid().ToString("D")),
            [], ct);
        await File.WriteAllTextAsync(
            Path.Combine(sessionDirectory, UploadService.PendingFinalMarkerPrefix + Guid.NewGuid().ToString("D")),
            "files/2026/09", ct);
        await File.WriteAllTextAsync(
            Path.Combine(sessionDirectory, UploadService.PendingFinalMarkerPrefix + Guid.NewGuid().ToString("D")),
            "../outside.bin", ct);
        await File.WriteAllTextAsync(
            Path.Combine(sessionDirectory, UploadService.PendingFinalStagingPrefix + Guid.NewGuid().ToString("D")),
            "interrupted-before-publication", ct);

        await scope.Maintenance.RunGarbageCollectionAsync(ct);

        Assert.Equal("EXPIRED", await scope.StatusAsync(sessionId));
        Assert.True(File.Exists(referenced));
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(unrelated));
        Assert.Equal([2], await File.ReadAllBytesAsync(outside, ct));
        Assert.False(Directory.Exists(sessionDirectory));
    }

    private static Task WriteMarkerAsync(string sessionDirectory, string finalPath) =>
        File.WriteAllTextAsync(
            Path.Combine(sessionDirectory, UploadService.PendingFinalMarkerPrefix + Guid.NewGuid().ToString("D")),
            Path.GetRelativePath(Path.GetDirectoryName(sessionDirectory) is { } temp
                    ? Directory.GetParent(temp)!.FullName
                    : throw new InvalidOperationException(), finalPath)
                .Replace(Path.DirectorySeparatorChar, '/'));

    private sealed class FilesDatabaseScope : IAsyncDisposable
    {
        private readonly MySqlConnection administration;
        private readonly string databaseName;
        private readonly string disposableRoot;

        private FilesDatabaseScope(
            MySqlConnection administration,
            string databaseName,
            string disposableRoot,
            AppDb database,
            FilesMaintenanceService maintenance,
            UploadService upload)
        {
            this.administration = administration;
            this.databaseName = databaseName;
            this.disposableRoot = disposableRoot;
            Database = database;
            Maintenance = maintenance;
            Upload = upload;
            StorageRoot = Path.Combine(disposableRoot, "storage");
        }

        public AppDb Database { get; }
        public FilesMaintenanceService Maintenance { get; }
        public UploadService Upload { get; }
        public string StorageRoot { get; }

        public static async Task<FilesDatabaseScope> CreateOrSkipAsync(CancellationToken ct)
        {
            var raw = Environment.GetEnvironmentVariable("YF_TEST_DATABASE_URL");
            if (string.IsNullOrWhiteSpace(raw)) Assert.Skip("YF_TEST_DATABASE_URL is not set");
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "mysql")
                throw new InvalidOperationException("YF_TEST_DATABASE_URL must be a mysql:// URL");
            if (uri.Host is not ("127.0.0.1" or "localhost" or "::1"))
                throw new InvalidOperationException("File recovery tests only allow local MySQL");

            var adminOptions = new MySqlConnectionStringBuilder
            {
                Server = uri.Host,
                Port = (uint)(uri.IsDefaultPort ? 3306 : uri.Port),
                UserID = Uri.UnescapeDataString(uri.UserInfo.Split(':', 2)[0]),
                Password = uri.UserInfo.Contains(':')
                    ? Uri.UnescapeDataString(uri.UserInfo.Split(':', 2)[1])
                    : string.Empty,
                DateTimeKind = MySqlDateTimeKind.Utc,
                SslMode = MySqlSslMode.None
            };
            var administration = new MySqlConnection(adminOptions.ConnectionString);
            await administration.OpenAsync(ct);
            var databaseName = "yf_test_dotnet_files_" + Guid.NewGuid().ToString("N");
            try
            {
                await administration.ExecuteAsync(new CommandDefinition(
                    $"CREATE DATABASE `{databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci",
                    cancellationToken: ct));
                var appBuilder = new MySqlConnectionStringBuilder(adminOptions.ConnectionString) { Database = databaseName };
                var disposableRoot = Path.Combine(Path.GetTempPath(), "yf_dotnet_files_" + Guid.NewGuid().ToString("N"));
                var storageRoot = Path.Combine(disposableRoot, "storage");
                Directory.CreateDirectory(storageRoot);
                var options = new AppOptions
                {
                    ConnectionString = appBuilder.ConnectionString,
                    StorageRoot = storageRoot,
                    WorkerEnabled = false
                };
                var database = new AppDb(options);
                await using (var connection = await database.OpenAsync(ct))
                {
                    await connection.ExecuteAsync(new CommandDefinition("""
                        CREATE TABLE system_configs(
                            cfg_key VARCHAR(100) PRIMARY KEY, cfg_value TEXT NOT NULL
                        );
                        CREATE TABLE users(
                            id BIGINT UNSIGNED PRIMARY KEY, employee_no VARCHAR(50) NOT NULL,
                            user_type VARCHAR(20) NOT NULL, supplier_id BIGINT UNSIGNED NULL,
                            status VARCHAR(20) NOT NULL, must_change_password BOOLEAN NOT NULL,
                            email VARCHAR(255) NULL, real_name VARCHAR(100) NOT NULL
                        );
                        CREATE TABLE roles(
                            id BIGINT UNSIGNED PRIMARY KEY, name VARCHAR(100) NOT NULL,
                            status VARCHAR(20) NOT NULL, is_built_in BOOLEAN NOT NULL
                        );
                        CREATE TABLE permissions(
                            id BIGINT UNSIGNED PRIMARY KEY, code VARCHAR(100) NOT NULL
                        );
                        CREATE TABLE user_roles(user_id BIGINT UNSIGNED NOT NULL, role_id BIGINT UNSIGNED NOT NULL);
                        CREATE TABLE role_permissions(role_id BIGINT UNSIGNED NOT NULL, permission_id BIGINT UNSIGNED NOT NULL);
                        CREATE TABLE projects(
                            id BIGINT UNSIGNED PRIMARY KEY, supplier_id BIGINT UNSIGNED NOT NULL,
                            created_by BIGINT UNSIGNED NOT NULL, status VARCHAR(30) NOT NULL,
                            confirm_side VARCHAR(20) NULL, name VARCHAR(100) NOT NULL
                        );
                        CREATE TABLE project_members(project_id BIGINT UNSIGNED NOT NULL, user_id BIGINT UNSIGNED NOT NULL);
                        CREATE TABLE audit_logs(
                            id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                            user_id BIGINT UNSIGNED NULL, employee_no VARCHAR(50) NULL,
                            action VARCHAR(100) NOT NULL, target_type VARCHAR(100) NULL,
                            target_id VARCHAR(100) NULL, detail JSON NULL, ip VARCHAR(100) NULL,
                            created_at DATETIME(6) NOT NULL
                        );
                        CREATE TABLE upload_sessions(
                            id VARCHAR(36) PRIMARY KEY, project_id BIGINT UNSIGNED NOT NULL,
                            uploader_id BIGINT UNSIGNED NOT NULL, file_name VARCHAR(255) NOT NULL,
                            file_size BIGINT UNSIGNED NOT NULL, file_md5 VARCHAR(32) NULL,
                            chunk_size INT UNSIGNED NOT NULL, total_chunks INT UNSIGNED NOT NULL,
                            temp_dir VARCHAR(512) NOT NULL, status VARCHAR(16) NOT NULL,
                            result_file_id BIGINT UNSIGNED NULL, expires_at DATETIME NOT NULL,
                            created_at DATETIME NOT NULL, updated_at DATETIME NOT NULL
                        );
                        CREATE TABLE files(
                            id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                            storage_path VARCHAR(1024) NOT NULL, status VARCHAR(20) NOT NULL,
                            deleted_at DATETIME(6) NULL
                        );
                        INSERT INTO system_configs(cfg_key,cfg_value) VALUES
                            ('security.management_lock','1'),
                            ('upload.chunk_size','262144'),
                            ('upload.max_file_size','1048576'),
                            ('upload.allowed_exts','bin'),
                            ('notify.enabled','false');
                        INSERT INTO users
                            (id,employee_no,user_type,supplier_id,status,must_change_password,email,real_name)
                            VALUES(1,'T001','INTERNAL',NULL,'ACTIVE',FALSE,NULL,'Tester');
                        INSERT INTO roles(id,name,status,is_built_in) VALUES(1,'文件测试','ACTIVE',FALSE);
                        INSERT INTO permissions(id,code) VALUES(1,'file:upload'),(2,'project:list');
                        INSERT INTO user_roles(user_id,role_id) VALUES(1,1);
                        INSERT INTO role_permissions(role_id,permission_id) VALUES(1,1),(1,2);
                        INSERT INTO projects(id,supplier_id,created_by,status,confirm_side,name)
                            VALUES(1,1,1,'IN_PROGRESS',NULL,'File Test');
                        """, cancellationToken: ct));
                }
                var maintenance = new FilesMaintenanceService(
                    database, options, NullLogger<FilesMaintenanceService>.Instance);
                var upload = new UploadService(database, options,
                    new AuditService(Array.Empty<IProjectAuditCapture>()),
                    NullLogger<UploadService>.Instance);
                return new FilesDatabaseScope(
                    administration, databaseName, disposableRoot, database, maintenance, upload);
            }
            catch
            {
                try { await administration.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`"); }
                finally { await administration.DisposeAsync(); }
                throw;
            }
        }

        public async Task InsertSessionAsync(string id, string status, DateTime expiresAt)
        {
            Directory.CreateDirectory(FileStorage.SessionDirectory(StorageRoot, id));
            await using var connection = await Database.OpenAsync();
            await connection.ExecuteAsync("""
                INSERT INTO upload_sessions
                    (id,project_id,uploader_id,file_name,file_size,file_md5,chunk_size,total_chunks,
                     temp_dir,status,result_file_id,expires_at,created_at,updated_at)
                VALUES(@Id,1,1,'sample.bin',1,NULL,1,1,@TempDir,@Status,NULL,@ExpiresAt,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
                """, new
            {
                Id = id,
                TempDir = FileStorage.SessionDirectory(StorageRoot, id),
                Status = status,
                ExpiresAt = expiresAt
            });
        }

        public DefaultHttpContext Context(long? contentLength = null)
        {
            var context = new DefaultHttpContext();
            context.Items[typeof(CurrentUser)] = new CurrentUser(1, "T001", "INTERNAL", null);
            context.Request.ContentLength = contentLength;
            return context;
        }

        public Task KillConnectionAsync(int serverThread, CancellationToken ct) =>
            administration.ExecuteAsync(new CommandDefinition(
                $"KILL CONNECTION {serverThread}", cancellationToken: ct));

        public async Task<string> StatusAsync(string id)
        {
            await using var connection = await Database.OpenAsync();
            return await connection.QuerySingleAsync<string>(
                "SELECT status FROM upload_sessions WHERE id=@Id", new { Id = id });
        }

        public string CreateFinal(string name)
        {
            var directory = Path.Combine(StorageRoot, "files", "2026", "09");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name);
            File.WriteAllBytes(path, [1]);
            return path;
        }

        public async Task ReferenceFileAsync(string finalPath)
        {
            await using var connection = await Database.OpenAsync();
            await connection.ExecuteAsync(
                "INSERT INTO files(storage_path,status,deleted_at) VALUES(@Path,'AVAILABLE',NULL)",
                new { Path = Path.GetRelativePath(StorageRoot, finalPath).Replace(Path.DirectorySeparatorChar, '/') });
        }

        public async ValueTask DisposeAsync()
        {
            try { await administration.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`"); }
            finally
            {
                await administration.DisposeAsync();
                try { Directory.Delete(disposableRoot, recursive: true); } catch { }
            }
        }
    }

    private sealed class BlockingOneByteStream : Stream
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int emitted;

        public Task WaitUntilReadAsync(CancellationToken ct) => entered.Task.WaitAsync(ct);
        public void Release() => released.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            entered.TrySetResult();
            await released.Task.WaitAsync(ct);
            if (Interlocked.Exchange(ref emitted, 1) != 0) return 0;
            buffer.Span[0] = 1;
            return 1;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Files;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

/// <summary>
/// Supplier accounts withdraw their own files and messages within the configured minutes
/// (withdraw.supplier_file_minutes / withdraw.supplier_message_minutes).
/// </summary>
[Collection(ConnectionLifecycleCollectionDefinition.Name)]
public sealed class SupplierWithdrawalTests
{
    private static readonly CurrentUser Supplier = new(201, "wd-supplier", "SUPPLIER", 100);
    private static readonly CurrentUser SupplierPeer = new(202, "wd-supplier-peer", "SUPPLIER", 100);
    private static readonly CurrentUser Owner = new(101, "wd-owner", "INTERNAL", null);
    private static readonly CurrentUser Admin = new(1, "admin", "INTERNAL", null);

    [Fact]
    public void TheWindowOnlyOpensForTheSupplierAuthorWhileItLasts()
    {
        var created = new DateTime(2026, 11, 5, 8, 0, 0, DateTimeKind.Utc);
        Assert.Equal(created.AddMinutes(10), SupplierWithdrawal.Deadline(Supplier, 201, created, 10, created.AddMinutes(9)));
        Assert.Null(SupplierWithdrawal.Deadline(Supplier, 201, created, 10, created.AddMinutes(10)));
        Assert.Null(SupplierWithdrawal.Deadline(Supplier, 202, created, 10, created));
        Assert.Null(SupplierWithdrawal.Deadline(Owner, 101, created, 10, created));
        Assert.Null(SupplierWithdrawal.Deadline(Supplier, 201, created, 0, created));
        Assert.Contains("当前不允许撤回文件", Assert.Throws<ApiException>(() =>
            SupplierWithdrawal.RequireOpen(Supplier, 201, created, 0, created, "文件")).Message);
        Assert.Contains("已超过 10 分钟", Assert.Throws<ApiException>(() =>
            SupplierWithdrawal.RequireOpen(Supplier, 201, created, 10, created.AddMinutes(11), "留言")).Message);
    }

    [Fact]
    public void ASummaryEntryIsCutOutByItsLinkLine()
    {
        const string header = "项目：P\n以下文件请登录平台查看并下载（邮件不含附件）：\n\n";
        var first = "- a.step\n  http://x/projects/7?tab=files&target=3\n  上传人：账号 S\n";
        var second = "- b.step\n  http://x/projects/7?tab=files&target=31\n  上传人：账号 S\n";
        var body = header + first + second;
        Assert.Equal(header + second, ProjectNotificationService.RemoveSummaryEntry(body, "/projects/7?tab=files&target=3\n"));
        Assert.Equal(header + first, ProjectNotificationService.RemoveSummaryEntry(body, "/projects/7?tab=files&target=31\n"));
        Assert.Null(ProjectNotificationService.RemoveSummaryEntry(body, "/projects/7?tab=files&target=4\n"));
    }

    [Fact(Timeout = 120_000)]
    public async Task SupplierWithdrawsAndRestoresOwnFilesWhileInternalStaffKeepReadingThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await LocalDatabaseScope.CreateOrSkipAsync("supplier_file_withdraw", ct);
        await database.InitializeAsync(ct);
        await database.SeedAsync(Fixture + """
            UPDATE projects SET material_review_status='RETURNED', material_review_file_id=3004 WHERE id=1001;
            INSERT INTO file_blobs(id,sha256,size_bytes,storage_path,state,created_at)
            VALUES(900,REPEAT('a',64),4,'blobs/sha256/aa/fixture','READY',UTC_TIMESTAMP(6));
            INSERT INTO files(id,project_id,uploader_id,blob_id,direction,original_name,stored_name,ext,size_bytes,storage_path,status,created_at)
            VALUES
                (3001,1001,201,900,'S2C','in-time.step','s1','step',4,'blobs/sha256/aa/fixture','AVAILABLE',UTC_TIMESTAMP(6)-INTERVAL 2 MINUTE),
                (3002,1001,201,900,'S2C','too-late.step','s2','step',4,'blobs/sha256/aa/fixture','AVAILABLE',UTC_TIMESTAMP(6)-INTERVAL 11 MINUTE),
                (3003,1001,202,900,'S2C','peer.step','s3','step',4,'blobs/sha256/aa/fixture','AVAILABLE',UTC_TIMESTAMP(6)-INTERVAL 1 MINUTE),
                (3004,1001,201,900,'S2C','returned.step','s4','step',4,'blobs/sha256/aa/fixture','AVAILABLE',UTC_TIMESTAMP(6)-INTERVAL 1 MINUTE),
                (3005,1001,101,900,'C2S','company.step','s5','step',4,'blobs/sha256/aa/fixture','AVAILABLE',UTC_TIMESTAMP(6)-INTERVAL 1 MINUTE),
                (3006,1002,201,900,'S2C','completed.step','s6','step',4,'blobs/sha256/aa/fixture','AVAILABLE',UTC_TIMESTAMP(6)-INTERVAL 1 MINUTE),
                (3007,1002,201,900,'S2C','completed-withdrawn.step','s7','step',4,'blobs/sha256/aa/fixture','WITHDRAWN',UTC_TIMESTAMP(6)-INTERVAL 1 MINUTE);
            INSERT INTO email_outbox(id,event_type,project_id,dedupe_key,recipient_user_id,recipient_email,subject,body,status,retry_count,next_attempt_at,created_at)
            VALUES
                (1,'FILE_UPLOADED',1001,'file-summary:1001:101:S2C',101,'owner@example.invalid','摘要',
                 CONCAT('项目：P\n方向：供应商 → 公司\n以下文件请登录平台查看并下载（邮件不含附件）：\n\n',
                        '- in-time.step\n  http://x/projects/1001?tab=files&target=3001\n  上传人：账号 wd-supplier\n',
                        '- peer.step\n  http://x/projects/1001?tab=files&target=3003\n  上传人：账号 wd-supplier-peer\n'),
                 'PENDING',0,UTC_TIMESTAMP(3)+INTERVAL 1 MINUTE,UTC_TIMESTAMP(3)),
                (2,'FILE_UPLOADED',1001,'file-summary:1001:102:S2C',102,'other@example.invalid','摘要',
                 CONCAT('项目：P\n方向：供应商 → 公司\n以下文件请登录平台查看并下载（邮件不含附件）：\n\n',
                        '- in-time.step\n  http://x/projects/1001?tab=files&target=3001\n  上传人：账号 wd-supplier\n'),
                 'PENDING',0,UTC_TIMESTAMP(3)+INTERVAL 1 MINUTE,UTC_TIMESTAMP(3)),
                (3,'FILE_UPLOADED',1001,NULL,101,'owner@example.invalid','摘要',
                 'x\n\n- in-time.step\n  http://x/projects/1001?tab=files&target=3001\n','SENT',0,NULL,UTC_TIMESTAMP(3));
            """, ct);
        database.WriteStoredFile("blobs/sha256/aa/fixture", "STEP");
        var files = Files(database.Options);

        var listed = await ListedAsync(files, Supplier, 1001, ct);
        Assert.Equal(JsonValueKind.String, listed[3001].GetProperty("withdrawableUntil").ValueKind);
        Assert.All(new ulong[] { 3002, 3003, 3004, 3005 }, id =>
            Assert.Equal(JsonValueKind.Null, listed[id].GetProperty("withdrawableUntil").ValueKind));
        Assert.All((await ListedAsync(files, Owner, 1001, ct)).Values,
            item => Assert.Equal(JsonValueKind.Null, item.GetProperty("withdrawableUntil").ValueKind));

        await AssertRefused(409, "已超过 10 分钟", () => files.WithdrawAsync(Context(Supplier), 3002, ct));
        await AssertRefused(403, "只能撤回本账号上传的文件", () => files.WithdrawAsync(Context(Supplier), 3003, ct));
        await AssertRefused(403, "只能撤回本账号上传的文件", () => files.WithdrawAsync(Context(Supplier), 3005, ct));
        await AssertRefused(409, "负责人已审核", () => files.WithdrawAsync(Context(Supplier), 3004, ct));
        await AssertRefused(409, "项目当前不可撤回文件", () => files.WithdrawAsync(Context(Supplier), 3006, ct));
        await AssertRefused(403, null, () => files.WithdrawAsync(Context(Owner), 3005, ct));

        await files.WithdrawAsync(Context(Supplier), 3001, ct);
        await AssertRefused(404, null, () => files.WithdrawAsync(Context(Supplier), 3001, ct));

        // Internal staff still list, download and preview the withdrawn file; the supplier that withdrew it sees
        // it as restorable but cannot read it; another account of the same supplier no longer sees it at all.
        var internalView = (await ListedAsync(files, Owner, 1001, ct))[3001];
        Assert.True(internalView.GetProperty("withdrawn").GetBoolean());
        Assert.False(internalView.GetProperty("canRestore").GetBoolean());
        Assert.NotNull(await files.StreamAsync(Context(Owner), 3001, inline: false, ct));
        var supplierView = (await ListedAsync(files, Supplier, 1001, ct))[3001];
        Assert.True(supplierView.GetProperty("withdrawn").GetBoolean());
        Assert.True(supplierView.GetProperty("canRestore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, supplierView.GetProperty("withdrawableUntil").ValueKind);
        await AssertRefused(404, null, () => files.StreamAsync(Context(Supplier), 3001, inline: false, ct));
        await AssertRefused(400, "不可用", () => files.BatchDownloadAsync(Context(Supplier), new BatchDownloadRequest([3001]), ct));
        Assert.DoesNotContain(3001UL, (await ListedAsync(files, SupplierPeer, 1001, ct)).Keys);

        await using (var conn = await database.Database.OpenAsync(ct))
        {
            Assert.Equal("WITHDRAWN", await conn.ExecuteScalarAsync<string>("SELECT status FROM files WHERE id=3001"));
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM audit_logs WHERE action='FILE_WITHDRAW' AND target_id='3001' AND user_id=201"));
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM project_activities WHERE project_id=1001 AND action='WITHDRAW'"));
            var mail = (await conn.QueryAsync<(ulong Id, string Status, string Body, string? DedupeKey, string? LastError)>(
                "SELECT id,status,body,dedupe_key,last_error FROM email_outbox ORDER BY id")).ToArray();
            // The two-entry summary keeps the other file; the single-entry summary is cancelled; sent mail is untouched.
            Assert.Equal("PENDING", mail[0].Status);
            Assert.DoesNotContain("target=3001", mail[0].Body);
            Assert.Contains("target=3003", mail[0].Body);
            Assert.Equal(("CANCELLED", null, ProjectNotificationService.WithdrawnMailReason), (mail[1].Status, mail[1].DedupeKey, mail[1].LastError));
            Assert.Equal("SENT", mail[2].Status);
            Assert.Contains("target=3001", mail[2].Body);
        }

        // Only the account that withdrew it restores it, at any time while the subproject is in progress.
        await AssertRefused(404, null, () => files.RestoreAsync(Context(SupplierPeer), 3001, ct));
        await AssertRefused(403, null, () => files.RestoreAsync(Context(Owner), 3001, ct));
        await AssertRefused(409, "项目当前不可恢复文件", () => files.RestoreAsync(Context(Supplier), 3007, ct));
        await database.SeedAsync("UPDATE files SET created_at=UTC_TIMESTAMP(6)-INTERVAL 3 DAY WHERE id=3001", ct);
        await files.RestoreAsync(Context(Supplier), 3001, ct);
        await AssertRefused(409, "无需恢复", () => files.RestoreAsync(Context(Supplier), 3001, ct));
        Assert.False((await ListedAsync(files, Supplier, 1001, ct))[3001].GetProperty("withdrawn").GetBoolean());
        Assert.NotNull(await files.StreamAsync(Context(Supplier), 3001, inline: false, ct));

        // Internal deletion also covers a withdrawn file.
        await database.SeedAsync("UPDATE files SET status='WITHDRAWN' WHERE id=3001", ct);
        await files.DeleteAsync(Context(Admin), 3001, ct);
        await using (var conn = await database.Database.OpenAsync(ct))
        {
            Assert.Equal("DELETED", await conn.ExecuteScalarAsync<string>("SELECT status FROM files WHERE id=3001"));
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM audit_logs WHERE action='FILE_RESTORE' AND target_id='3001' AND user_id=201"));
        }

        // A zero-minute setting disables withdrawal outright.
        database.Options.SupplierFileWithdrawMinutes = 0;
        await database.SeedAsync("UPDATE projects SET material_review_status=NULL, material_review_file_id=NULL WHERE id=1001", ct);
        await AssertRefused(409, "当前不允许撤回文件", () => files.WithdrawAsync(Context(Supplier), 3004, ct));
        Assert.All((await ListedAsync(files, Supplier, 1001, ct)).Values,
            item => Assert.Equal(JsonValueKind.Null, item.GetProperty("withdrawableUntil").ValueKind));
    }

    [Fact(Timeout = 120_000)]
    public async Task CopiedFilesNeverOpenANewWithdrawalWindowButOriginalUploadsRemainWithdrawable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await LocalDatabaseScope.CreateOrSkipAsync("supplier_copied_file_withdraw", ct);
        await database.InitializeAsync(ct);
        await database.SeedAsync(Fixture + """
            UPDATE projects SET status='IN_PROGRESS' WHERE id=1002;
            INSERT INTO projects(id,project_group_id,name,supplier_id,status,created_by,responsible_user_id)
            VALUES(1003,5001,'再次复制子项目',100,'IN_PROGRESS',1,101);
            INSERT INTO files(id,project_id,uploader_id,direction,original_name,stored_name,ext,size_bytes,storage_path,status,created_at)
            VALUES
                (3101,1002,201,'S2C','old-source.step','old-source','step',4,'unused','AVAILABLE',UTC_TIMESTAMP(6)-INTERVAL 1 DAY),
                (3102,1001,201,'S2C','copied.step','copied','step',4,'unused','AVAILABLE',UTC_TIMESTAMP(6)),
                (3103,1003,201,'S2C','nested-copy.step','nested-copy','step',4,'unused','AVAILABLE',UTC_TIMESTAMP(6)),
                (3104,1002,201,'S2C','fresh-source.step','fresh-source','step',4,'unused','AVAILABLE',UTC_TIMESTAMP(6)),
                (3105,1001,201,'S2C','fresh-copy.step','fresh-copy','step',4,'unused','AVAILABLE',UTC_TIMESTAMP(6));
            INSERT INTO project_copies(id,source_project_id,target_project_id,source_project_name,target_project_name,copied_by,copied_by_name,file_count,total_bytes,created_at)
            VALUES(7001,1002,1001,'source','copy',1,'admin',2,8,UTC_TIMESTAMP(6)),
                  (7002,1001,1003,'copy','nested',1,'admin',1,4,UTC_TIMESTAMP(6));
            INSERT INTO file_copy_refs(copy_id,source_file_id,target_file_id,source_file_name,target_file_name,created_at)
            VALUES(7001,3101,3102,'old-source.step','copied.step',UTC_TIMESTAMP(6)),
                  (7002,3102,3103,'copied.step','nested-copy.step',UTC_TIMESTAMP(6)),
                  (7001,3104,3105,'fresh-source.step','fresh-copy.step',UTC_TIMESTAMP(6));
            """, ct);
        var files = Files(database.Options);

        foreach (var (projectId, fileId) in new (ulong, ulong)[] { (1001, 3102), (1003, 3103), (1001, 3105) })
        {
            var item = (await ListedAsync(files, Supplier, projectId, ct))[fileId];
            Assert.True(item.GetProperty("isCopiedReference").GetBoolean());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("withdrawableUntil").ValueKind);
            await AssertRefused(409, "复制带入的文件不能撤回", () => files.WithdrawAsync(Context(Supplier), fileId, ct));
        }

        // Being a copy's source must not prevent withdrawal of a genuine recent upload.
        Assert.Equal(JsonValueKind.String,
            (await ListedAsync(files, Supplier, 1002, ct))[3104].GetProperty("withdrawableUntil").ValueKind);
        await files.WithdrawAsync(Context(Supplier), 3104, ct);
        await using (var conn = await database.Database.OpenAsync(ct))
        {
            Assert.Equal(3, await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM files WHERE id IN (3102,3103,3105) AND status='AVAILABLE'"));
        }

        // A copy withdrawn before this rule was introduced can still be recovered by its supplier.
        await database.SeedAsync("UPDATE files SET status='WITHDRAWN' WHERE id=3102", ct);
        Assert.True((await ListedAsync(files, Supplier, 1001, ct))[3102].GetProperty("canRestore").GetBoolean());
        await files.RestoreAsync(Context(Supplier), 3102, ct);
        Assert.Equal(JsonValueKind.Null,
            (await ListedAsync(files, Supplier, 1001, ct))[3102].GetProperty("withdrawableUntil").ValueKind);
    }

    [Fact(Timeout = 120_000)]
    public async Task SupplierWithdrawsAndRestoresOwnMessagesWhileInternalStaffKeepReadingThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await LocalDatabaseScope.CreateOrSkipAsync("supplier_message_withdraw", ct);
        await database.InitializeAsync(ct);
        await database.SeedAsync(Fixture + """
            INSERT INTO messages(id,project_id,sender_id,content,status,created_at)
            VALUES
                (4001,1001,201,'发错了的留言','NORMAL',UTC_TIMESTAMP(6)-INTERVAL 1 MINUTE),
                (4002,1001,201,'很久以前的留言','NORMAL',UTC_TIMESTAMP(6)-INTERVAL 20 MINUTE),
                (4003,1001,202,'同事的留言','NORMAL',UTC_TIMESTAMP(6)-INTERVAL 1 MINUTE),
                (4004,1002,201,'已完成项目的留言','NORMAL',UTC_TIMESTAMP(6)-INTERVAL 1 MINUTE),
                (4005,1001,101,'负责人的留言','NORMAL',UTC_TIMESTAMP(6)-INTERVAL 1 MINUTE),
                (4007,1002,201,'已完成项目里撤回的留言','WITHDRAWN',UTC_TIMESTAMP(6)-INTERVAL 1 MINUTE);
            INSERT INTO message_images(id,message_id,original_name,stored_name,ext,size_bytes,mime_type,storage_path)
            VALUES(1,4001,'shot.png','shot.png','png',4,'image/png','message-images/shot.png');
            INSERT INTO email_outbox(id,event_type,project_id,dedupe_key,recipient_user_id,recipient_email,subject,body,status,retry_count,next_attempt_at,created_at)
            VALUES(1,'MESSAGE_CREATED',1001,'message-summary:1001:101',101,'owner@example.invalid','留言',
                   CONCAT('项目：P\n以下留言请登录平台查看（本邮件由系统自动发送）：\n\n',
                          '- 留言人：账号 wd-supplier\n  内容：发错了的留言\n  http://x/projects/1001?tab=messages&target=4001\n'),
                   'PENDING',0,UTC_TIMESTAMP(3)+INTERVAL 1 MINUTE,UTC_TIMESTAMP(3));
            """, ct);
        database.WriteStoredFile("message-images/shot.png", "PNG!");
        var service = new MessageService(new AuditService([new ProjectActivityService()]), database.Options);
        await using var conn = await database.Database.OpenAsync(ct);
        async Task<MessageResponse> Seen(CurrentUser viewer, ulong id) =>
            (await service.ListAsync(conn, viewer, 1001, 1, 50, null, null, ct)).List.Single(item => item.Id == id);

        var before = (await service.ListAsync(conn, Supplier, 1001, 1, 50, null, null, ct)).List.ToDictionary(item => item.Id);
        Assert.NotNull(before[4001].WithdrawableUntil);
        Assert.Null(before[4002].WithdrawableUntil);
        Assert.Null(before[4003].WithdrawableUntil);
        Assert.Null(before[4005].WithdrawableUntil);

        await AssertRefused(409, "已超过 10 分钟", () => service.WithdrawAsync(conn, Supplier, 4002, null, ct));
        await AssertRefused(403, "只能撤回本账号发表的留言", () => service.WithdrawAsync(conn, Supplier, 4003, null, ct));
        await AssertRefused(409, "项目当前不可撤回留言", () => service.WithdrawAsync(conn, Supplier, 4004, null, ct));
        await AssertRefused(403, null, () => service.WithdrawAsync(conn, Owner, 4005, null, ct));

        await service.WithdrawAsync(conn, Supplier, 4001, null, ct);
        await AssertRefused(404, null, () => service.WithdrawAsync(conn, Supplier, 4001, null, ct));

        // Internal staff keep the content and images, marked withdrawn; suppliers get a placeholder, and only the
        // account that withdrew it may restore it.
        var internalView = await Seen(Owner, 4001);
        Assert.Equal((MessageService.WithdrawnStatus, "发错了的留言", 1, false),
            (internalView.Status, internalView.Content, internalView.Images.Count, internalView.CanRestore));
        Assert.NotNull(await service.GetImageAsync(conn, Owner, 4001, 1, ct));
        var supplierView = await Seen(Supplier, 4001);
        Assert.Equal((MessageService.WithdrawnStatus, "", 0, true, (DateTime?)null),
            (supplierView.Status, supplierView.Content, supplierView.Images.Count, supplierView.CanRestore, supplierView.WithdrawableUntil));
        var peerView = await Seen(SupplierPeer, 4001);
        Assert.Equal(("", false), (peerView.Content, peerView.CanRestore));
        await AssertRefused(404, null, () => service.GetImageAsync(conn, Supplier, 4001, 1, ct));

        Assert.Equal(("WITHDRAWN", 201UL), await conn.QuerySingleAsync<(string, ulong)>(
            "SELECT status,deleted_by FROM messages WHERE id=4001"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM audit_logs WHERE action='MESSAGE_WITHDRAW' AND target_id='4001' AND user_id=201"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM project_activities WHERE project_id=1001 AND action='WITHDRAW'"));
        Assert.Equal("CANCELLED", await conn.ExecuteScalarAsync<string>("SELECT status FROM email_outbox WHERE id=1"));

        await AssertRefused(404, null, () => service.RestoreAsync(conn, SupplierPeer, 4001, null, ct));
        await AssertRefused(403, null, () => service.RestoreAsync(conn, Owner, 4001, null, ct));
        await AssertRefused(409, "项目当前不可恢复留言", () => service.RestoreAsync(conn, Supplier, 4007, null, ct));
        await conn.ExecuteAsync("UPDATE messages SET created_at=UTC_TIMESTAMP(6)-INTERVAL 3 DAY WHERE id=4001");
        await service.RestoreAsync(conn, Supplier, 4001, null, ct);
        await AssertRefused(409, "无需恢复", () => service.RestoreAsync(conn, Supplier, 4001, null, ct));
        var restored = await Seen(Supplier, 4001);
        Assert.Equal(("NORMAL", "发错了的留言", 1), (restored.Status, restored.Content, restored.Images.Count));
        Assert.Equal(("NORMAL", (ulong?)null), await conn.QuerySingleAsync<(string, ulong?)>(
            "SELECT status,deleted_by FROM messages WHERE id=4001"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM project_activities WHERE project_id=1001 AND action='RESTORE'"));

        // Internal deletion also covers a withdrawn message.
        await conn.ExecuteAsync("UPDATE messages SET status='WITHDRAWN' WHERE id=4001");
        await service.DeleteAsync(conn, Admin, 4001, null, ct);
        Assert.Equal("DELETED", await conn.ExecuteScalarAsync<string>("SELECT status FROM messages WHERE id=4001"));

        database.Options.SupplierMessageWithdrawMinutes = 0;
        await conn.ExecuteAsync("""
            INSERT INTO messages(id,project_id,sender_id,content,status,created_at)
            VALUES(4006,1001,201,'刚发的留言','NORMAL',UTC_TIMESTAMP(6));
            """);
        await AssertRefused(409, "当前不允许撤回留言", () => service.WithdrawAsync(conn, Supplier, 4006, null, ct));
    }

    private static async Task<Dictionary<ulong, JsonElement>> ListedAsync(FileService files, CurrentUser viewer, ulong projectId, CancellationToken ct) =>
        Json(await files.ListAsync(Context(viewer), projectId, ct)).GetProperty("list").EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetUInt64());

    private const string Fixture = """
        UPDATE users SET must_change_password=0 WHERE id=1;
        INSERT INTO suppliers(id,name,status,created_by) VALUES(100,'撤回供应商','ACTIVE',1);
        INSERT INTO roles(id,name,description,is_built_in,status)
        VALUES(9001,'撤回负责人','撤回测试',0,'ACTIVE'),(9002,'撤回供应商人员','撤回测试',0,'ACTIVE');
        INSERT INTO role_permissions(role_id,permission_id)
        SELECT 9001,p.id FROM permissions p WHERE p.code IN ('project:list','file:upload','file:download','message:create')
        UNION ALL
        SELECT 9002,p.id FROM permissions p WHERE p.code IN ('project:list','file:upload','file:download','message:create');
        INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password)
        VALUES
            (101,'wd-owner','unused','负责人','owner@example.invalid','INTERNAL',NULL,'ACTIVE',0),
            (201,'wd-supplier','unused','供应商账号','','SUPPLIER',100,'ACTIVE',0),
            (202,'wd-supplier-peer','unused','供应商另一账号','','SUPPLIER',100,'ACTIVE',0);
        INSERT INTO user_roles(user_id,role_id) VALUES(101,9001),(201,9002),(202,9002);
        INSERT INTO project_groups(id,name,supplier_id,status,created_by,responsible_user_id)
        VALUES(5001,'撤回主项目',100,'IN_PROGRESS',1,101);
        INSERT INTO projects(id,project_group_id,name,supplier_id,status,created_by,responsible_user_id)
        VALUES(1001,5001,'撤回子项目',100,'IN_PROGRESS',1,101),(1002,5001,'已完成子项目',100,'COMPLETED',1,101);

        """;

    private static FileService Files(AppOptions options)
    {
        var database = new AppDb(options);
        var audit = new AuditService([new ProjectActivityService()]);
        var identity = new IdentityService(EfTestSupport.DbContextFactory(options), options, new LoginRateLimiter(),
            new TokenService(options), new PermissionService(), audit);
        return new FileService(database, options, audit, new BatchDownloadLimiter(),
            new MediaGrantService(options), new DownloadGrantService(), identity, NullLogger<FileService>.Instance);
    }

    private static DefaultHttpContext Context(CurrentUser actor)
    {
        var context = new DefaultHttpContext();
        context.Items[typeof(CurrentUser)] = actor;
        return context;
    }

    private static async Task AssertRefused(int status, string? message, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(status, error.Status);
        if (message is not null) Assert.Contains(message, error.Message);
    }

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value, TestJson.Web);

    private sealed class LocalDatabaseScope(
        MySqlConnection administration,
        string databaseName,
        AppDb database,
        AppOptions options) : IAsyncDisposable
    {
        public AppDb Database { get; } = database;
        public AppOptions Options { get; } = options;

        public static async Task<LocalDatabaseScope> CreateOrSkipAsync(string purpose, CancellationToken ct)
        {
            var administrationOptions = LocalMySqlTestServer.AdministrationOrSkip("Supplier withdrawal tests");
            var administration = new MySqlConnection(administrationOptions.ConnectionString);
            await administration.OpenAsync(ct);
            var databaseName = $"yf_t_{Guid.NewGuid():N}";
            try
            {
                await administration.ExecuteAsync(new CommandDefinition(
                    $"CREATE DATABASE `{databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci",
                    cancellationToken: ct));
                var options = new AppOptions
                {
                    ConnectionString = new MySqlConnectionStringBuilder(administrationOptions.ConnectionString)
                    {
                        Database = databaseName,
                        MaximumPoolSize = 2,
                        MinimumPoolSize = 0
                    }.ConnectionString,
                    StorageRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "yf_supplier_withdraw_" + Guid.NewGuid().ToString("N"))).FullName,
                    WorkerEnabled = false,
                    JwtSecret = "supplier-withdraw-test-secret-at-least-32-bytes",
                };
                return new(administration, databaseName, new AppDb(options), options);
            }
            catch
            {
                try { await administration.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`"); }
                finally { await administration.DisposeAsync(); }
                throw;
            }
        }

        public async Task InitializeAsync(CancellationToken ct) =>
            await SchemaBootstrap.InitializeEmptyAsync(Database, "Withdraw#" + Guid.NewGuid().ToString("N")[..8] + "!", ct);

        public async Task SeedAsync(string sql, CancellationToken ct)
        {
            await using var connection = await Database.OpenAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
        }

        public void WriteStoredFile(string relative, string content)
        {
            var path = Path.Combine(Options.StorageRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public async ValueTask DisposeAsync()
        {
            try { await administration.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`"); }
            finally
            {
                await administration.DisposeAsync();
                try { Directory.Delete(Options.StorageRoot, recursive: true); } catch (IOException) { }
            }
        }
    }
}

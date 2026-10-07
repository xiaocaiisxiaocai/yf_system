using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Oem.Data;

namespace Yf.Api.Infrastructure;

internal static class BootstrapSeedCatalog
{
    internal const ulong AdministratorRoleId = 1;
    internal static readonly string[] IdentityRevisionTriggers =
    [
        "trg_identity_users_insert", "trg_identity_users_update", "trg_identity_users_delete",
        "trg_identity_suppliers_insert", "trg_identity_suppliers_update", "trg_identity_suppliers_delete",
        "trg_identity_refresh_tokens_insert", "trg_identity_refresh_tokens_update", "trg_identity_refresh_tokens_delete",
    ];

    private static readonly (ulong Id, string Code, string Name, string Type, ulong? ParentId, int SortNo)[] Permissions =
    [
        (1, "dashboard", "工作台", "MENU", null, 0),
        (2, "project:list", "项目列表", "MENU", null, 1),
        (3, "supplier:list", "供应商管理", "MENU", null, 2),
        (4, "org:user", "用户管理", "MENU", null, 3),
        (5, "org:dept", "组织架构", "MENU", null, 4),
        (6, "rbac:role", "角色权限", "MENU", null, 5),
        (7, "log:audit", "操作日志", "MENU", null, 6),
        (8, "system:config", "系统参数", "MENU", null, 7),
        (9, "project:create", "创建项目", "ACTION", 2, 0),
        (10, "project:update", "编辑项目", "ACTION", 2, 1),
        (11, "project:delete", "删除项目", "ACTION", 2, 2),
        (12, "project:status", "项目状态变更", "ACTION", 2, 3),
        (14, "project:view_all", "查看全部项目", "ACTION", 2, 5),
        (18, "file:upload", "上传文件", "ACTION", 2, 9),
        (19, "file:download", "下载文件", "ACTION", 2, 10),
        (20, "file:preview", "预览文件", "ACTION", 2, 11),
        (21, "file:delete", "删除文件", "ACTION", 2, 12),
        (22, "message:create", "发表留言", "ACTION", 2, 13),
        (23, "message:delete_any", "删除留言", "ACTION", 2, 14),
        (24, "supplier:manage", "供应商管理", "ACTION", 3, 15),
        (25, "supplier:delete", "删除供应商", "ACTION", 3, 16),
        (26, "supplier:account", "供应商账号管理", "ACTION", 3, 17),
        (27, "supplier:account_delete", "删除供应商账号", "ACTION", 3, 18),
        (28, "user:manage", "用户管理", "ACTION", 4, 19),
        (29, "user:delete", "删除用户", "ACTION", 4, 20),
        (30, "dept:manage", "组织架构管理", "ACTION", 5, 21),
        (31, "dept:delete", "删除组织节点", "ACTION", 5, 22),
        (32, "role:manage", "角色管理", "ACTION", 6, 23),
        (33, "role:delete", "删除角色", "ACTION", 6, 24),
        (34, "log:view", "日志查看", "ACTION", 7, 25),
        (36, "config:manage", "系统参数管理", "ACTION", 8, 27),
        (45, "project:submit", "提交项目验收", "ACTION", 2, 40),
        (46, "project:confirm", "确认/驳回项目", "ACTION", 2, 41),
        (47, "project:withdraw", "撤回项目验收", "ACTION", 2, 42),
        (48, "system:dict", "数据字典", "MENU", null, 8),
        (49, "dict:manage", "数据字典管理", "ACTION", 48, 28),
        (50, "project:transfer", "变更项目负责人", "ACTION", 2, 43),
        // 100..113 belonged to the historical OEM migration and may have been
        // reused after DropOemPlatform. Fresh databases use a new reserved range;
        // upgrades insert by code and let MySQL allocate IDs.
        (200, "oem", "OEM 文件传递", "MENU", null, 9),
        (201, "oem:transfer_create", "创建公司出站传递", "ACTION", 200, 50),
        (202, "oem:transfer_view", "查看 OEM 传递单", "ACTION", 200, 51),
        (203, "oem:file_download", "下载/预览 OEM 文件", "ACTION", 200, 52),
        (204, "oem:flow_approve", "处理 OEM 审批", "ACTION", 200, 53),
        (205, "oem:approval_recover", "OEM 审批异常处置", "ACTION", 200, 54),
        (206, "oem:company_manage", "OEM 厂商管理", "ACTION", 200, 55),
        (207, "oem:account_manage", "OEM 账号管理", "ACTION", 200, 56),
        (208, "oem:flow_template_manage", "OEM 审批模板管理", "ACTION", 200, 57),
        (209, "oem:retention_template_manage", "OEM 删除策略管理", "ACTION", 200, 58),
        (210, "oem:file_policy_manage", "OEM 文件策略管理", "ACTION", 200, 59),
        (211, "oem:notify_manage", "OEM 邮件提醒管理", "ACTION", 200, 60),
        (212, "oem:audit_view", "OEM 审计查看", "ACTION", 200, 61),
        (213, "dept:leader_manage", "维护组织主管", "ACTION", 5, 62),
        (214, "oem:company_delete", "删除 OEM 厂商", "ACTION", 200, 63),
        (215, "oem:account_delete", "删除 OEM 账号", "ACTION", 200, 64),
    ];

    private static readonly (string Key, string Value, string Description)[] Configs =
    [
        ("notify.enabled", "true", "邮件通知总开关"),
        ("notify.internal.enabled", "true", "是否向公司内部员工发送邮件提醒"),
        ("notify.supplier.enabled", "true", "是否向外部企业（供应商）发送邮件提醒"),
        ("notify.event.message_created", "true", "新留言邮件提醒"),
        ("notify.event.file_uploaded", "true", "新文件邮件提醒"),
        ("notify.event.project_submitted", "true", "提交验收邮件提醒"),
        ("notify.event.project_confirmed", "true", "验收通过邮件提醒"),
        ("notify.event.project_rejected", "true", "验收驳回邮件提醒"),
        ("notify.event.project_withdrawn", "true", "撤回验收邮件提醒"),
        ("security.management_lock", "1", "权限与高风险操作事务锁"),
        ("upload.allowed_exts", "step,stp,iges,igs,stl,obj,fbx,dwg,dxf,pdf,doc,docx,xls,xlsx,xlsm,xlsb,ppt,pptx,png,jpg,jpeg,gif,webp,bmp,zip,rar,7z,mp4,webm,ogv", "允许上传的扩展名白名单"),
        ("upload.chunk_size", "10485760", "分片大小（字节，默认 10MB）"),
        ("upload.max_file_size", "2147483648", "单文件大小上限（字节，默认 2GB）"),
        ("oem.upload.allowed_exts.internal_to_oem", "7z,doc,docx,dwg,dxf,igs,iges,jpeg,jpg,obj,pdf,png,ppt,pptx,rar,step,stl,stp,xls,xlsx,zip", "OEM 公司出站允许的扩展名"),
        ("oem.upload.allowed_exts.oem_to_internal", "7z,doc,docx,dwg,dxf,igs,iges,jpeg,jpg,obj,pdf,png,ppt,pptx,rar,step,stl,stp,xls,xlsx,zip", "OEM 入站允许的扩展名"),
        ("oem.upload.max_file_size", "2147483648", "OEM 单文件大小上限（字节）"),
        ("oem.upload.max_transfer_size", "10737418240", "OEM 单传递单总大小上限（字节）"),
        ("oem.upload.chunk_size", "16777216", "OEM 分片大小（字节）"),
        ("oem.upload.max_concurrent_per_company", "4", "每个 OEM 厂商同时进行的上传数"),
        ("oem.upload.max_storage_per_company", "107374182400", "每个 OEM 厂商在线占用上限（字节）"),
        ("oem.upload.session_ttl_hours", "24", "OEM 上传会话有效期（小时）"),
        ("oem.validation.archive_max_entries", "10000", "压缩包条目上限"),
        ("oem.validation.archive_max_depth", "5", "压缩包嵌套层级上限"),
        ("oem.validation.archive_max_expanded_bytes", "21474836480", "压缩包解压后总量上限（字节）"),
        ("oem.validation.archive_max_ratio", "100", "压缩比上限"),
        ("oem.validation.max_retries", "5", "文件校验错误最大重试次数"),
        ("oem.validation.blocked_retention_hours", "72", "校验未通过文件暂存保留时间（小时）"),
        ("oem.transfer.draft_ttl_hours", "720", "未发送草稿保留期限（小时）"),
        ("oem.download.max_ranges_per_session", "256", "单个下载会话合并后区间数上限"),
        ("oem.download.max_parallel_per_session", "8", "单个下载会话并行请求上限"),
        ("oem.download.max_requests_per_minute", "600", "每账号每分钟下载请求上限"),
        ("oem.download.session_ttl_minutes", "1440", "下载会话绝对寿命（分钟）"),
        ("oem.download.idle_timeout_seconds", "300", "下载无进展取消期限（秒）"),
        ("oem.download.max_duration_minutes", "720", "单个下载请求最长时长（分钟）"),
        ("oem.download.purge_drain_minutes", "30", "到期后活动下载排空期限（分钟）"),
        ("oem.notify.enabled", "true", "OEM 邮件提醒总开关"),
        ("oem.notify.event.approval_pending", "true", "OEM 待审批邮件"),
        ("oem.notify.event.transfer_released", "true", "OEM 发布通知接收人邮件"),
        ("oem.notify.event.sender_result", "true", "OEM 发送结果通知发送人邮件"),
        ("oem.notify.event.receipt", "false", "OEM 接收与删除进度通知发送人邮件"),
        ("oem.storage.reconcile_required", "", "OEM 存储恢复核对标记（系统维护）"),
    ];

    internal static async Task SeedEmptyAsync(YfDbContext db, string passwordHash, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted, ct);
        db.Roles.Add(new Role
        {
            Id = AdministratorRoleId,
            Name = BuiltInRoleNames.SystemAdministrator,
            IsBuiltIn = true,
            Status = AccountStatuses.Active,
        });
        db.Permissions.AddRange(Permissions.Select(row => new Permission
        {
            Id = row.Id,
            Code = row.Code,
            Name = row.Name,
            Type = row.Type,
            ParentId = row.ParentId,
            SortNo = row.SortNo,
        }));
        db.SystemConfigs.AddRange(Configs.Select(row => new SystemConfig
        {
            CfgKey = row.Key,
            CfgValue = row.Value,
            Description = row.Description,
        }));
        db.ProjectDictionaries.AddRange(
            new ProjectDictionary { Type = "PRIORITY", Name = "高", SortNo = 10, Status = AccountStatuses.Active },
            new ProjectDictionary { Type = "PRIORITY", Name = "普通", SortNo = 20, Status = AccountStatuses.Active },
            new ProjectDictionary { Type = "PRIORITY", Name = "低", SortNo = 30, Status = AccountStatuses.Active });
        var now = DateTime.UtcNow;
        db.OemFlowTemplates.Add(new OemFlowTemplate
        {
            Id = 1,
            Name = "默认审批模板",
            IsDefault = true,
            Status = AccountStatuses.Active,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.OemFlowTemplateNodes.AddRange(
            new OemFlowTemplateNode
            {
                TemplateId = 1,
                SortNo = 1,
                Name = "课别主管审批",
                ApproverSource = "SECTION_LEADER",
                ApprovalMode = "SINGLE",
                SelfPolicy = "SKIP",
                Enabled = true,
            },
            new OemFlowTemplateNode
            {
                TemplateId = 1,
                SortNo = 2,
                Name = "部门主管审批",
                ApproverSource = "DEPARTMENT_LEADER",
                ApprovalMode = "SINGLE",
                SelfPolicy = "SKIP",
                Enabled = false,
            });
        db.OemRetentionTemplates.Add(new OemRetentionTemplate
        {
            Id = 1,
            Name = "不自动删除",
            Mode = "KEEP",
            Status = AccountStatuses.Active,
            CreatedAt = now,
            UpdatedAt = now,
        });
        var administrator = new User
        {
            EmployeeNo = "admin",
            PasswordHash = passwordHash,
            RealName = "系统管理员",
            Email = string.Empty,
            UserType = UserTypes.Internal,
            Status = AccountStatuses.Active,
            MustChangePassword = true,
            FailedLoginAttempts = 0,
        };
        db.Users.Add(administrator);
        await db.SaveChangesAsync(ct);

        var allPermissionIds = await db.Permissions.Select(permission => permission.Id).ToArrayAsync(ct);
        db.RolePermissions.AddRange(allPermissionIds.Select(permissionId => new RolePermission
        {
            RoleId = AdministratorRoleId,
            PermissionId = permissionId,
        }));
        db.UserRoles.Add(new UserRole { UserId = administrator.Id, RoleId = AdministratorRoleId });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    internal static async Task ValidateRuntimeSeedAsync(YfDbContext db, CancellationToken ct)
    {
        if (await db.SystemConfigs.CountAsync(config => config.CfgKey == "security.management_lock", ct) != 1)
            throw new InvalidOperationException("Database permission gate missing.");
        var revision = await db.SystemConfigs.Where(config => config.CfgKey == "security.identity_revision")
            .Select(config => config.CfgValue).SingleOrDefaultAsync(ct);
        if (revision is null || !ulong.TryParse(revision, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            throw new InvalidOperationException("Database identity revision missing or invalid.");
        var triggers = await db.Database.SqlQuery<string>($"""
                SELECT TRIGGER_NAME AS Value
                FROM information_schema.TRIGGERS
                WHERE TRIGGER_SCHEMA=DATABASE() AND TRIGGER_NAME LIKE 'trg_identity_%'
                """)
            .ToArrayAsync(ct);
        if (!IdentityRevisionTriggers.ToHashSet(StringComparer.Ordinal).SetEquals(triggers))
            throw new InvalidOperationException("Database identity revision triggers missing or unexpected.");
    }
}

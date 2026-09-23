using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Infrastructure;

internal static class BootstrapSeedCatalog
{
    internal const ulong AdministratorRoleId = 1;

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
        (35, "log:delete", "删除日志", "ACTION", 7, 26),
        (36, "config:manage", "系统参数管理", "ACTION", 8, 27),
        (45, "project:submit", "提交项目验收", "ACTION", 2, 40),
        (46, "project:confirm", "确认/驳回项目", "ACTION", 2, 41),
        (47, "project:withdraw", "撤回项目验收", "ACTION", 2, 42),
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
        ("upload.allowed_exts", "step,stp,iges,igs,stl,obj,fbx,dwg,dxf,pdf,doc,docx,xls,xlsx,ppt,pptx,png,jpg,jpeg,gif,webp,bmp,zip,rar,7z,mp4,webm,ogv", "允许上传的扩展名白名单"),
        ("upload.chunk_size", "10485760", "分片大小（字节，默认 10MB）"),
        ("upload.max_file_size", "2147483648", "单文件大小上限（字节，默认 2GB）"),
    ];

    internal static async Task SeedEmptyAsync(YfDbContext db, string passwordHash, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted, ct);
        db.Roles.Add(new Role
        {
            Id = AdministratorRoleId,
            Name = "系统管理员",
            IsBuiltIn = true,
            Status = "ACTIVE",
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
            new ProjectDictionary { Type = "PRIORITY", Name = "高", SortNo = 10, Status = "ACTIVE" },
            new ProjectDictionary { Type = "PRIORITY", Name = "普通", SortNo = 20, Status = "ACTIVE" },
            new ProjectDictionary { Type = "PRIORITY", Name = "低", SortNo = 30, Status = "ACTIVE" });
        var administrator = new User
        {
            EmployeeNo = "admin",
            PasswordHash = passwordHash,
            RealName = "系统管理员",
            Email = string.Empty,
            UserType = "INTERNAL",
            Status = "ACTIVE",
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
    }
}

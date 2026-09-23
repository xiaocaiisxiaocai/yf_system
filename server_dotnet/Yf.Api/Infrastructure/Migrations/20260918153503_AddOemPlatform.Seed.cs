using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    // Reference data owned by this migration. It is written as literal SQL (not
    // by referencing live application constants) so the migration stays an
    // immutable snapshot even if later code renames or regroups the values.
    public partial class AddOemPlatform
    {
        private static readonly (int Id, string Code, string Name, string Type, int? ParentId, int SortNo)[] OemPermissions =
        [
            (100, "oem", "OEM 文件传递", "MENU", null, 8),
            (101, "oem:transfer_create", "创建公司出站传递", "ACTION", 100, 50),
            (102, "oem:transfer_view", "查看 OEM 传递单", "ACTION", 100, 51),
            (103, "oem:file_download", "下载/预览 OEM 文件", "ACTION", 100, 52),
            (104, "oem:flow_approve", "处理 OEM 审批", "ACTION", 100, 53),
            (105, "oem:approval_recover", "OEM 审批异常处置", "ACTION", 100, 54),
            (106, "oem:company_manage", "OEM 厂商管理", "ACTION", 100, 55),
            (107, "oem:account_manage", "OEM 账号管理", "ACTION", 100, 56),
            (108, "oem:flow_template_manage", "OEM 审批模板管理", "ACTION", 100, 57),
            (109, "oem:retention_template_manage", "OEM 删除策略管理", "ACTION", 100, 58),
            (110, "oem:file_policy_manage", "OEM 文件策略管理", "ACTION", 100, 59),
            (111, "oem:notify_manage", "OEM 邮件提醒管理", "ACTION", 100, 60),
            (112, "oem:audit_view", "OEM 审计查看", "ACTION", 100, 61),
            (113, "dept:leader_manage", "维护组织主管", "ACTION", 5, 62),
        ];

        private static readonly (string Key, string Value, string Description)[] OemConfigs =
        [
            ("oem.upload.allowed_exts.internal_to_oem", "7z,doc,docx,dwg,dxf,igs,iges,jpeg,jpg,obj,pdf,png,ppt,pptx,rar,step,stl,stp,xls,xlsx,zip", "OEM 公司出站允许的扩展名"),
            ("oem.upload.allowed_exts.oem_to_internal", "7z,doc,docx,dwg,dxf,igs,iges,jpeg,jpg,obj,pdf,png,ppt,pptx,rar,step,stl,stp,xls,xlsx,zip", "OEM 入站允许的扩展名"),
            ("oem.upload.max_file_size", "2147483648", "OEM 单文件大小上限（字节）"),
            ("oem.upload.max_transfer_size", "10737418240", "OEM 单传递单总大小上限（字节）"),
            ("oem.upload.chunk_size", "16777216", "OEM 分片大小（字节）"),
            ("oem.upload.max_concurrent_per_company", "4", "每个 OEM 厂商同时进行的上传数"),
            ("oem.upload.max_storage_per_company", "107374182400", "每个 OEM 厂商在线占用上限（字节）"),
            ("oem.upload.session_ttl_hours", "24", "OEM 上传会话有效期（小时）"),
            ("oem.scan.archive_max_entries", "10000", "压缩包条目上限"),
            ("oem.scan.archive_max_depth", "5", "压缩包嵌套层级上限"),
            ("oem.scan.archive_max_expanded_bytes", "21474836480", "压缩包解压后总量上限（字节）"),
            ("oem.scan.archive_max_ratio", "100", "压缩比上限"),
            ("oem.scan.max_retries", "5", "扫描错误最大重试次数"),
            ("oem.scan.max_signature_age_hours", "48", "病毒库最长未更新时间（小时）"),
            ("oem.scan.block_on_stale_signatures", "false", "病毒库过期时是否暂停放行"),
            ("oem.scan.blocked_retention_hours", "72", "不安全文件隔离保留时间（小时）"),
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

        internal static void SeedOemReferenceData(MigrationBuilder migrationBuilder)
        {
            foreach (var p in OemPermissions)
            {
                migrationBuilder.Sql(
                    "INSERT INTO permissions(id,code,name,type,parent_id,sort_no) VALUES(" +
                    $"{p.Id},'{p.Code}','{p.Name}','{p.Type}',{(p.ParentId is null ? "NULL" : p.ParentId.ToString())},{p.SortNo})");
            }
            // Existing databases: the built-in administrator keeps full authority. On a
            // brand-new database the role does not exist yet; BootstrapSeedCatalog grants
            // every permission when it creates the administrator afterwards.
            migrationBuilder.Sql(
                "INSERT IGNORE INTO role_permissions(role_id,permission_id) " +
                "SELECT r.id,p.id FROM roles r JOIN permissions p ON p.id BETWEEN 100 AND 113 " +
                "WHERE r.is_built_in=1 AND r.name='系统管理员'");
            foreach (var c in OemConfigs)
            {
                migrationBuilder.Sql(
                    "INSERT INTO system_configs(cfg_key,cfg_value,description) VALUES(" +
                    $"'{c.Key}','{c.Value}','{c.Description}')");
            }
            migrationBuilder.Sql(
                "INSERT INTO oem_flow_templates(id,name,is_default,status,concurrency_version,created_by,created_at,updated_at) " +
                "VALUES(1,'默认审批模板',1,'ACTIVE',0,NULL,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3))");
            migrationBuilder.Sql(
                "INSERT INTO oem_flow_template_nodes(template_id,sort_no,name,approver_source,approval_mode,self_policy,enabled) VALUES" +
                "(1,1,'课别主管审批','SECTION_LEADER','SINGLE','SKIP',1)," +
                "(1,2,'部门主管审批','DEPARTMENT_LEADER','SINGLE','SKIP',0)");
            migrationBuilder.Sql(
                "INSERT INTO oem_retention_templates(id,name,mode,release_ttl_minutes,receipt_grace_minutes,status,concurrency_version,created_by,created_at,updated_at) " +
                "VALUES(1,'不自动删除','KEEP',NULL,NULL,'ACTIVE',0,NULL,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3))");
        }

        internal static void RemoveOemReferenceData(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM role_permissions WHERE permission_id BETWEEN 100 AND 113");
            migrationBuilder.Sql("DELETE FROM permissions WHERE id BETWEEN 100 AND 113");
            migrationBuilder.Sql("DELETE FROM system_configs WHERE cfg_key LIKE 'oem.%'");
        }
    }
}

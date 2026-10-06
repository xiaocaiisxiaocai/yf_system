using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    // Reference data owned by RestoreOemPlatform. Existing databases receive it
    // by code/key without fixed permission IDs; empty databases are populated by
    // BootstrapSeedCatalog after the full migration chain completes.
    public partial class RestoreOemPlatform
    {
        private static void SeedOemReferenceData(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO permissions(code,name,type,parent_id,sort_no)
                SELECT 'oem','OEM 文件传递','MENU',NULL,9 FROM DUAL
                WHERE EXISTS (SELECT 1 FROM permissions WHERE code='dashboard')
                  AND NOT EXISTS (SELECT 1 FROM permissions WHERE code='oem');
                """);
            migrationBuilder.Sql("""
                INSERT INTO permissions(code,name,type,parent_id,sort_no)
                SELECT seed.code,seed.name,'ACTION',parent.id,seed.sort_no
                FROM (
                    SELECT 'oem:transfer_create' code,'创建公司出站传递' name,'oem' parent_code,50 sort_no
                    UNION ALL SELECT 'oem:transfer_view','查看 OEM 传递单','oem',51
                    UNION ALL SELECT 'oem:file_download','下载/预览 OEM 文件','oem',52
                    UNION ALL SELECT 'oem:flow_approve','处理 OEM 审批','oem',53
                    UNION ALL SELECT 'oem:approval_recover','OEM 审批异常处置','oem',54
                    UNION ALL SELECT 'oem:company_manage','OEM 厂商管理','oem',55
                    UNION ALL SELECT 'oem:account_manage','OEM 账号管理','oem',56
                    UNION ALL SELECT 'oem:flow_template_manage','OEM 审批模板管理','oem',57
                    UNION ALL SELECT 'oem:retention_template_manage','OEM 删除策略管理','oem',58
                    UNION ALL SELECT 'oem:file_policy_manage','OEM 文件策略管理','oem',59
                    UNION ALL SELECT 'oem:notify_manage','OEM 邮件提醒管理','oem',60
                    UNION ALL SELECT 'oem:audit_view','OEM 审计查看','oem',61
                    UNION ALL SELECT 'dept:leader_manage','维护组织主管','org:dept',62
                ) seed
                INNER JOIN permissions parent ON parent.code=seed.parent_code
                WHERE EXISTS (SELECT 1 FROM permissions WHERE code='dashboard')
                  AND NOT EXISTS (SELECT 1 FROM permissions existing WHERE existing.code=seed.code);
                """);
            migrationBuilder.Sql("""
                INSERT IGNORE INTO role_permissions(role_id,permission_id)
                SELECT r.id,p.id
                FROM roles r
                INNER JOIN permissions p ON p.code='oem' OR p.code LIKE 'oem:%' OR p.code='dept:leader_manage'
                WHERE r.is_built_in=1 AND r.name='系统管理员';
                """);

            migrationBuilder.Sql("""
                INSERT INTO system_configs(cfg_key,cfg_value,description)
                SELECT seed.cfg_key,seed.cfg_value,seed.description
                FROM (
                    SELECT 'oem.upload.allowed_exts.internal_to_oem' cfg_key,'7z,doc,docx,dwg,dxf,igs,iges,jpeg,jpg,obj,pdf,png,ppt,pptx,rar,step,stl,stp,xls,xlsx,zip' cfg_value,'OEM 公司出站允许的扩展名' description
                    UNION ALL SELECT 'oem.upload.allowed_exts.oem_to_internal','7z,doc,docx,dwg,dxf,igs,iges,jpeg,jpg,obj,pdf,png,ppt,pptx,rar,step,stl,stp,xls,xlsx,zip','OEM 入站允许的扩展名'
                    UNION ALL SELECT 'oem.upload.max_file_size','2147483648','OEM 单文件大小上限（字节）'
                    UNION ALL SELECT 'oem.upload.max_transfer_size','10737418240','OEM 单传递单总大小上限（字节）'
                    UNION ALL SELECT 'oem.upload.chunk_size','16777216','OEM 分片大小（字节）'
                    UNION ALL SELECT 'oem.upload.max_concurrent_per_company','4','每个 OEM 厂商同时进行的上传数'
                    UNION ALL SELECT 'oem.upload.max_storage_per_company','107374182400','每个 OEM 厂商在线占用上限（字节）'
                    UNION ALL SELECT 'oem.upload.session_ttl_hours','24','OEM 上传会话有效期（小时）'
                    UNION ALL SELECT 'oem.scan.archive_max_entries','10000','压缩包条目上限'
                    UNION ALL SELECT 'oem.scan.archive_max_depth','5','压缩包嵌套层级上限'
                    UNION ALL SELECT 'oem.scan.archive_max_expanded_bytes','21474836480','压缩包解压后总量上限（字节）'
                    UNION ALL SELECT 'oem.scan.archive_max_ratio','100','压缩比上限'
                    UNION ALL SELECT 'oem.scan.max_retries','5','扫描错误最大重试次数'
                    UNION ALL SELECT 'oem.scan.max_signature_age_hours','48','病毒库最长未更新时间（小时）'
                    UNION ALL SELECT 'oem.scan.block_on_stale_signatures','false','病毒库过期时是否暂停放行'
                    UNION ALL SELECT 'oem.scan.blocked_retention_hours','72','不安全文件隔离保留时间（小时）'
                    UNION ALL SELECT 'oem.transfer.draft_ttl_hours','720','未发送草稿保留期限（小时）'
                    UNION ALL SELECT 'oem.download.max_ranges_per_session','256','单个下载会话合并后区间数上限'
                    UNION ALL SELECT 'oem.download.max_parallel_per_session','8','单个下载会话并行请求上限'
                    UNION ALL SELECT 'oem.download.max_requests_per_minute','600','每账号每分钟下载请求上限'
                    UNION ALL SELECT 'oem.download.session_ttl_minutes','1440','下载会话绝对寿命（分钟）'
                    UNION ALL SELECT 'oem.download.idle_timeout_seconds','300','下载无进展取消期限（秒）'
                    UNION ALL SELECT 'oem.download.max_duration_minutes','720','单个下载请求最长时长（分钟）'
                    UNION ALL SELECT 'oem.download.purge_drain_minutes','30','到期后活动下载排空期限（分钟）'
                    UNION ALL SELECT 'oem.notify.enabled','true','OEM 邮件提醒总开关'
                    UNION ALL SELECT 'oem.notify.event.approval_pending','true','OEM 待审批邮件'
                    UNION ALL SELECT 'oem.notify.event.transfer_released','true','OEM 发布通知接收人邮件'
                    UNION ALL SELECT 'oem.notify.event.sender_result','true','OEM 发送结果通知发送人邮件'
                    UNION ALL SELECT 'oem.notify.event.receipt','false','OEM 接收与删除进度通知发送人邮件'
                    UNION ALL SELECT 'oem.storage.reconcile_required','','OEM 存储恢复核对标记（系统维护）'
                ) seed
                WHERE EXISTS (SELECT 1 FROM permissions WHERE code='dashboard')
                  AND NOT EXISTS (SELECT 1 FROM system_configs existing WHERE existing.cfg_key=seed.cfg_key);
                """);

            migrationBuilder.Sql("""
                INSERT INTO oem_flow_templates(id,name,is_default,status,concurrency_version,created_by,created_at,updated_at)
                SELECT 1,'默认审批模板',1,'ACTIVE',0,NULL,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3) FROM DUAL
                WHERE EXISTS (SELECT 1 FROM permissions WHERE code='dashboard')
                  AND NOT EXISTS (SELECT 1 FROM oem_flow_templates WHERE id=1 OR name='默认审批模板');
                """);
            migrationBuilder.Sql("""
                INSERT INTO oem_flow_template_nodes(template_id,sort_no,name,approver_source,approval_mode,self_policy,enabled)
                SELECT 1,seed.sort_no,seed.name,seed.approver_source,'SINGLE','SKIP',seed.enabled
                FROM (
                    SELECT 1 sort_no,'课别主管审批' name,'SECTION_LEADER' approver_source,1 enabled
                    UNION ALL SELECT 2,'部门主管审批','DEPARTMENT_LEADER',0
                ) seed
                WHERE EXISTS (SELECT 1 FROM permissions WHERE code='dashboard')
                  AND EXISTS (SELECT 1 FROM oem_flow_templates WHERE id=1)
                  AND NOT EXISTS (
                      SELECT 1 FROM oem_flow_template_nodes existing
                      WHERE existing.template_id=1 AND existing.sort_no=seed.sort_no);
                """);
            migrationBuilder.Sql("""
                INSERT INTO oem_retention_templates(id,name,mode,release_ttl_minutes,receipt_grace_minutes,status,concurrency_version,created_by,created_at,updated_at)
                SELECT 1,'不自动删除','KEEP',NULL,NULL,'ACTIVE',0,NULL,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3) FROM DUAL
                WHERE EXISTS (SELECT 1 FROM permissions WHERE code='dashboard')
                  AND NOT EXISTS (SELECT 1 FROM oem_retention_templates WHERE id=1 OR name='不自动删除');
                """);
        }

        private static void RemoveOemReferenceData(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE rp FROM role_permissions rp
                INNER JOIN permissions p ON p.id=rp.permission_id
                WHERE p.code='oem' OR p.code LIKE 'oem:%' OR p.code='dept:leader_manage';
                """);
            migrationBuilder.Sql("DELETE FROM permissions WHERE code LIKE 'oem:%' OR code='dept:leader_manage';");
            migrationBuilder.Sql("DELETE FROM permissions WHERE code='oem';");
            migrationBuilder.Sql("DELETE FROM system_configs WHERE cfg_key LIKE 'oem.%';");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <summary>
    /// The OEM business line is shown as "ODM/OEMA". Existing databases get the same permission names and
    /// configuration descriptions a fresh installation seeds; codes and keys stay unchanged. Data migration only.
    /// </summary>
    public partial class RenameOemLabelsToOdmOema : Migration
    {
        private static readonly (string Code, string Name)[] Permissions =
        [
            ("oem", "ODM/OEMA 文件传递"),
            ("oem:transfer_view", "查看 ODM/OEMA 传递单"),
            ("oem:file_download", "下载/预览 ODM/OEMA 文件"),
            ("oem:flow_approve", "处理 ODM/OEMA 审批"),
            ("oem:approval_recover", "ODM/OEMA 审批异常处置"),
            ("oem:company_manage", "ODM/OEMA 厂商管理"),
            ("oem:account_manage", "ODM/OEMA 账号管理"),
            ("oem:flow_template_manage", "ODM/OEMA 审批模板管理"),
            ("oem:retention_template_manage", "ODM/OEMA 删除策略管理"),
            ("oem:file_policy_manage", "ODM/OEMA 文件策略管理"),
            ("oem:notify_manage", "ODM/OEMA 邮件提醒管理"),
            ("oem:audit_view", "ODM/OEMA 审计查看"),
            ("oem:company_delete", "删除 ODM/OEMA 厂商"),
            ("oem:account_delete", "删除 ODM/OEMA 账号"),
        ];

        private static readonly (string Key, string Description)[] Configs =
        [
            ("oem.upload.allowed_exts.internal_to_oem", "ODM/OEMA 公司出站允许的扩展名"),
            ("oem.upload.allowed_exts.oem_to_internal", "ODM/OEMA 入站允许的扩展名"),
            ("oem.upload.max_file_size", "ODM/OEMA 单文件大小上限（字节）"),
            ("oem.upload.max_transfer_size", "ODM/OEMA 单传递单总大小上限（字节）"),
            ("oem.upload.chunk_size", "ODM/OEMA 分片大小（字节）"),
            ("oem.upload.max_concurrent_per_company", "每个 ODM/OEMA 厂商同时进行的上传数"),
            ("oem.upload.max_storage_per_company", "每个 ODM/OEMA 厂商在线占用上限（字节）"),
            ("oem.upload.session_ttl_hours", "ODM/OEMA 上传会话有效期（小时）"),
            ("oem.notify.enabled", "ODM/OEMA 邮件提醒总开关"),
            ("oem.notify.event.approval_pending", "ODM/OEMA 待审批邮件"),
            ("oem.notify.event.transfer_released", "ODM/OEMA 发布通知接收人邮件"),
            ("oem.notify.event.sender_result", "ODM/OEMA 发送结果通知发送人邮件"),
            ("oem.notify.event.receipt", "ODM/OEMA 接收与删除进度通知发送人邮件"),
            ("oem.storage.reconcile_required", "ODM/OEMA 存储恢复核对标记（系统维护）"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (code, name) in Permissions)
                migrationBuilder.Sql($"UPDATE permissions SET name='{name}' WHERE code='{code}';");
            foreach (var (key, description) in Configs)
                migrationBuilder.Sql($"UPDATE system_configs SET description='{description}' WHERE cfg_key='{key}';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Display text only: older versions read the codes and keys, so the new names are kept.
        }
    }
}

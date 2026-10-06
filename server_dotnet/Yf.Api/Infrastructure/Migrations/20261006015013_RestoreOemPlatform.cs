using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RestoreOemPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<ulong>(
                name: "oem_transfer_id",
                table: "email_outbox",
                type: "bigint unsigned",
                nullable: true);

            migrationBuilder.AddColumn<ulong>(
                name: "recipient_account_id",
                table: "email_outbox",
                type: "bigint unsigned",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recipient_realm",
                table: "email_outbox",
                type: "varchar(16)",
                maxLength: 16,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<ulong>(
                name: "leader_account_id",
                table: "departments",
                type: "bigint unsigned",
                nullable: true);

            migrationBuilder.AddColumn<ulong>(
                name: "actor_account_id",
                table: "audit_logs",
                type: "bigint unsigned",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "actor_realm",
                table: "audit_logs",
                type: "varchar(16)",
                maxLength: 16,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "oem_companies",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    contact_name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    contact_phone = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    contact_email = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    remark = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValue: "ACTIVE", collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_by = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oem_companies", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "oem_flow_templates",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    is_default = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValue: "ACTIVE", collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    concurrency_version = table.Column<ulong>(type: "bigint unsigned", nullable: false, defaultValue: 0ul),
                    created_by = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oem_flow_templates", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "oem_retention_templates",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    mode = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    release_ttl_minutes = table.Column<uint>(type: "int unsigned", nullable: true),
                    receipt_grace_minutes = table.Column<uint>(type: "int unsigned", nullable: true),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValue: "ACTIVE", collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    concurrency_version = table.Column<ulong>(type: "bigint unsigned", nullable: false, defaultValue: 0ul),
                    created_by = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oem_retention_templates", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "oem_accounts",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    employee_no = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    password_hash = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    real_name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    email = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    oem_company_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValue: "ACTIVE", collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    must_change_password = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: true),
                    failed_login_attempts = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    locked_until = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    last_login_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    last_login_ip = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_by = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oem_accounts", x => x.id);
                    table.ForeignKey(
                        name: "fk_oem_accounts_company",
                        column: x => x.oem_company_id,
                        principalTable: "oem_companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "oem_flow_template_nodes",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    template_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    sort_no = table.Column<int>(type: "int", nullable: false),
                    name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    approver_source = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    approval_mode = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    self_policy = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    enabled = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oem_flow_template_nodes", x => x.id);
                    table.ForeignKey(
                        name: "fk_oem_flow_nodes_template",
                        column: x => x.template_id,
                        principalTable: "oem_flow_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "oem_flow_template_scopes",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    template_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    department_id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oem_flow_template_scopes", x => x.id);
                    table.ForeignKey(
                        name: "fk_oem_flow_scopes_template",
                        column: x => x.template_id,
                        principalTable: "oem_flow_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "oem_refresh_tokens",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    account_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    session_id = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    token_hash = table.Column<string>(type: "char(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    session_expires_at = table.Column<DateTime>(type: "datetime(3)", nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime(3)", nullable: false),
                    revoked = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false),
                    ip = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oem_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_oem_refresh_tokens_account",
                        column: x => x.account_id,
                        principalTable: "oem_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "oem_transfers",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    direction = table.Column<string>(type: "varchar(24)", maxLength: 24, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    oem_company_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    title = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    description = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    internal_sender_user_id = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    oem_sender_account_id = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    lifecycle_status = table.Column<string>(type: "varchar(24)", maxLength: 24, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    retention_template_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    retention_mode = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    release_ttl_minutes = table.Column<uint>(type: "int unsigned", nullable: true),
                    receipt_grace_minutes = table.Column<uint>(type: "int unsigned", nullable: true),
                    manifest_sha256 = table.Column<string>(type: "char(64)", fixedLength: true, maxLength: 64, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    sent_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    released_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    expires_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    closed_reason = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    closed_by_realm = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    closed_by_id = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    closed_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(3)", nullable: false),
                    concurrency_version = table.Column<ulong>(type: "bigint unsigned", nullable: false, defaultValue: 0ul)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oem_transfers", x => x.id);
                    table.ForeignKey(
                        name: "fk_oem_transfers_company",
                        column: x => x.oem_company_id,
                        principalTable: "oem_companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_oem_transfers_oem_sender",
                        column: x => x.oem_sender_account_id,
                        principalTable: "oem_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_oem_transfers_retention",
                        column: x => x.retention_template_id,
                        principalTable: "oem_retention_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "oem_flow_template_node_users",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    node_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    user_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    role = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oem_flow_template_node_users", x => x.id);
                    table.ForeignKey(
                        name: "fk_oem_flow_node_users_node",
                        column: x => x.node_id,
                        principalTable: "oem_flow_template_nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "oem_flow_instances",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    transfer_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    template_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    initiator_user_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    initiator_section_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    status = table.Column<string>(type: "varchar(24)", maxLength: 24, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    template_snapshot = table.Column<string>(type: "json", nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    blocked_reason = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    current_sort_no = table.Column<int>(type: "int", nullable: true),
                    concurrency_version = table.Column<ulong>(type: "bigint unsigned", nullable: false, defaultValue: 0ul),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oem_flow_instances", x => x.id);
                    table.ForeignKey(
                        name: "fk_oem_flow_instances_template",
                        column: x => x.template_id,
                        principalTable: "oem_flow_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_oem_flow_instances_transfer",
                        column: x => x.transfer_id,
                        principalTable: "oem_transfers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "oem_transfer_files",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    transfer_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    uploaded_by_internal_user_id = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    uploaded_by_oem_account_id = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    original_name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    stored_name = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ext = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    mime_type = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    size_bytes = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    md5 = table.Column<string>(type: "char(32)", fixedLength: true, maxLength: 32, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    sha256 = table.Column<string>(type: "char(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    storage_path = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    payload_status = table.Column<string>(type: "varchar(24)", maxLength: 24, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    scan_status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    first_recipient_download_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    first_recipient_realm = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    first_recipient_id = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    purge_due_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    purge_reason = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    purge_lease_owner = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    purge_lease_until = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    purge_attempt_count = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    purge_next_attempt_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    purge_last_error = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    purged_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    concurrency_version = table.Column<ulong>(type: "bigint unsigned", nullable: false, defaultValue: 0ul),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oem_transfer_files", x => x.id);
                    table.ForeignKey(
                        name: "fk_oem_transfer_files_transfer",
                        column: x => x.transfer_id,
                        principalTable: "oem_transfers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "oem_upload_sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    transfer_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    uploader_realm = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    uploader_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    file_name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    file_size = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    file_md5 = table.Column<string>(type: "char(32)", fixedLength: true, maxLength: 32, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    chunk_size = table.Column<uint>(type: "int unsigned", nullable: false),
                    total_chunks = table.Column<uint>(type: "int unsigned", nullable: false),
                    temp_dir = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    reserved_bytes = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    result_file_id = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    expires_at = table.Column<DateTime>(type: "datetime(3)", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oem_upload_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_oem_upload_sessions_transfer",
                        column: x => x.transfer_id,
                        principalTable: "oem_transfers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "oem_flow_instance_nodes",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    instance_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    sort_no = table.Column<int>(type: "int", nullable: false),
                    name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    approver_source = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    approval_mode = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    skip_reason = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    used_fallback = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false),
                    completed_at = table.Column<DateTime>(type: "datetime(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oem_flow_instance_nodes", x => x.id);
                    table.ForeignKey(
                        name: "fk_oem_flow_instance_nodes_instance",
                        column: x => x.instance_id,
                        principalTable: "oem_flow_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "oem_download_sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    file_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    file_stored_name = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    file_sha256 = table.Column<string>(type: "char(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    actor_realm = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    actor_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    login_session_id = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    purpose = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    recipient_side = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    expected_size = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    last_progress_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    absolute_deadline = table.Column<DateTime>(type: "datetime(3)", nullable: false),
                    concurrency_version = table.Column<ulong>(type: "bigint unsigned", nullable: false, defaultValue: 0ul),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false),
                    completed_at = table.Column<DateTime>(type: "datetime(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oem_download_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_oem_download_sessions_file",
                        column: x => x.file_id,
                        principalTable: "oem_transfer_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "oem_file_promotions",
                columns: table => new
                {
                    id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    file_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    file_sha256 = table.Column<string>(type: "char(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    size_bytes = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    source_path = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    target_path = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    lease_owner = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    lease_until = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    attempt_count = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    next_attempt_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    last_error = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    concurrency_version = table.Column<ulong>(type: "bigint unsigned", nullable: false, defaultValue: 0ul),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false),
                    completed_at = table.Column<DateTime>(type: "datetime(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oem_file_promotions", x => x.id);
                    table.ForeignKey(
                        name: "fk_oem_promotions_file",
                        column: x => x.file_id,
                        principalTable: "oem_transfer_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "oem_file_scan_jobs",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    file_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    file_sha256 = table.Column<string>(type: "char(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    file_size_bytes = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    attempt_count = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    next_attempt_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    lease_owner = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    lease_until = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    engine_name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    engine_version = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    signature_version = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    threat_name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    last_error = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    concurrency_version = table.Column<ulong>(type: "bigint unsigned", nullable: false, defaultValue: 0ul),
                    started_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    completed_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oem_file_scan_jobs", x => x.id);
                    table.ForeignKey(
                        name: "fk_oem_scan_jobs_file",
                        column: x => x.file_id,
                        principalTable: "oem_transfer_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "oem_flow_tasks",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    instance_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    instance_node_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    approver_user_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    reason = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    replaces_task_id = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    reassign_reason = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    reassigned_by = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    decided_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    concurrency_version = table.Column<ulong>(type: "bigint unsigned", nullable: false, defaultValue: 0ul),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oem_flow_tasks", x => x.id);
                    table.ForeignKey(
                        name: "fk_oem_flow_tasks_instance",
                        column: x => x.instance_id,
                        principalTable: "oem_flow_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_oem_flow_tasks_node",
                        column: x => x.instance_node_id,
                        principalTable: "oem_flow_instance_nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "oem_download_leases",
                columns: table => new
                {
                    id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    session_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    file_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    owner = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    started_at = table.Column<DateTime>(type: "datetime(3)", nullable: false),
                    last_progress_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    lease_until = table.Column<DateTime>(type: "datetime(3)", nullable: false),
                    hard_deadline = table.Column<DateTime>(type: "datetime(3)", nullable: false),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oem_download_leases", x => x.id);
                    table.ForeignKey(
                        name: "fk_oem_download_leases_session",
                        column: x => x.session_id,
                        principalTable: "oem_download_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "oem_download_ranges",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    session_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    start_offset = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    end_offset = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oem_download_ranges", x => x.id);
                    table.ForeignKey(
                        name: "fk_oem_download_ranges_session",
                        column: x => x.session_id,
                        principalTable: "oem_download_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateIndex(
                name: "idx_outbox_oem_transfer",
                table: "email_outbox",
                column: "oem_transfer_id");

            migrationBuilder.CreateIndex(
                name: "idx_audit_actor_realm",
                table: "audit_logs",
                column: "actor_realm");

            migrationBuilder.CreateIndex(
                name: "idx_oem_accounts_company",
                table: "oem_accounts",
                column: "oem_company_id");

            migrationBuilder.CreateIndex(
                name: "uk_oem_accounts_employee_no",
                table: "oem_accounts",
                column: "employee_no",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uk_oem_companies_name",
                table: "oem_companies",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_oem_download_leases_file",
                table: "oem_download_leases",
                columns: new[] { "file_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_oem_download_leases_session_id",
                table: "oem_download_leases",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "idx_oem_download_ranges_session",
                table: "oem_download_ranges",
                columns: new[] { "session_id", "start_offset" });

            migrationBuilder.CreateIndex(
                name: "idx_oem_download_sessions_actor",
                table: "oem_download_sessions",
                columns: new[] { "actor_realm", "actor_id" });

            migrationBuilder.CreateIndex(
                name: "idx_oem_download_sessions_file",
                table: "oem_download_sessions",
                columns: new[] { "file_id", "status" });

            migrationBuilder.CreateIndex(
                name: "idx_oem_promotions_file",
                table: "oem_file_promotions",
                column: "file_id");

            migrationBuilder.CreateIndex(
                name: "idx_oem_promotions_status_next",
                table: "oem_file_promotions",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "idx_oem_scan_jobs_file",
                table: "oem_file_scan_jobs",
                column: "file_id");

            migrationBuilder.CreateIndex(
                name: "idx_oem_scan_jobs_status_next",
                table: "oem_file_scan_jobs",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "uk_oem_flow_instance_nodes_sort",
                table: "oem_flow_instance_nodes",
                columns: new[] { "instance_id", "sort_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_oem_flow_instances_status",
                table: "oem_flow_instances",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_oem_flow_instances_template_id",
                table: "oem_flow_instances",
                column: "template_id");

            migrationBuilder.CreateIndex(
                name: "uk_oem_flow_instances_transfer",
                table: "oem_flow_instances",
                column: "transfer_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_oem_flow_tasks_approver",
                table: "oem_flow_tasks",
                columns: new[] { "approver_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "idx_oem_flow_tasks_instance",
                table: "oem_flow_tasks",
                column: "instance_id");

            migrationBuilder.CreateIndex(
                name: "idx_oem_flow_tasks_node",
                table: "oem_flow_tasks",
                column: "instance_node_id");

            migrationBuilder.CreateIndex(
                name: "uk_oem_flow_node_users",
                table: "oem_flow_template_node_users",
                columns: new[] { "node_id", "role", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uk_oem_flow_nodes_template_sort",
                table: "oem_flow_template_nodes",
                columns: new[] { "template_id", "sort_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_oem_flow_scopes_template",
                table: "oem_flow_template_scopes",
                column: "template_id");

            migrationBuilder.CreateIndex(
                name: "uk_oem_flow_scopes_department",
                table: "oem_flow_template_scopes",
                column: "department_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uk_oem_flow_templates_name",
                table: "oem_flow_templates",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_oem_refresh_tokens_account",
                table: "oem_refresh_tokens",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "idx_oem_refresh_tokens_session",
                table: "oem_refresh_tokens",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "idx_oem_refresh_tokens_session_expires",
                table: "oem_refresh_tokens",
                column: "session_expires_at");

            migrationBuilder.CreateIndex(
                name: "uk_oem_refresh_tokens_hash",
                table: "oem_refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uk_oem_retention_templates_name",
                table: "oem_retention_templates",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_oem_transfer_files_purge_due",
                table: "oem_transfer_files",
                columns: new[] { "payload_status", "purge_due_at" });

            migrationBuilder.CreateIndex(
                name: "idx_oem_transfer_files_purge_retry",
                table: "oem_transfer_files",
                columns: new[] { "payload_status", "purge_next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "idx_oem_transfer_files_transfer",
                table: "oem_transfer_files",
                column: "transfer_id");

            migrationBuilder.CreateIndex(
                name: "uk_oem_transfer_files_stored_name",
                table: "oem_transfer_files",
                column: "stored_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_oem_transfers_company_status",
                table: "oem_transfers",
                columns: new[] { "oem_company_id", "lifecycle_status" });

            migrationBuilder.CreateIndex(
                name: "idx_oem_transfers_internal_sender",
                table: "oem_transfers",
                column: "internal_sender_user_id");

            migrationBuilder.CreateIndex(
                name: "idx_oem_transfers_oem_sender",
                table: "oem_transfers",
                column: "oem_sender_account_id");

            migrationBuilder.CreateIndex(
                name: "idx_oem_transfers_status_released",
                table: "oem_transfers",
                columns: new[] { "lifecycle_status", "released_at" });

            migrationBuilder.CreateIndex(
                name: "IX_oem_transfers_retention_template_id",
                table: "oem_transfers",
                column: "retention_template_id");

            migrationBuilder.CreateIndex(
                name: "idx_oem_upload_sessions_expiry",
                table: "oem_upload_sessions",
                columns: new[] { "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "idx_oem_upload_sessions_transfer",
                table: "oem_upload_sessions",
                columns: new[] { "transfer_id", "status" });

            SeedOemReferenceData(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DELETE FROM email_outbox " +
                "WHERE event_type LIKE 'OEM\\_%' OR recipient_realm='oem' OR oem_transfer_id IS NOT NULL");
            migrationBuilder.Sql(
                "DELETE FROM audit_logs " +
                "WHERE action LIKE 'OEM\\_%' OR actor_realm='oem'");
            RemoveOemReferenceData(migrationBuilder);

            migrationBuilder.DropTable(
                name: "oem_download_leases");

            migrationBuilder.DropTable(
                name: "oem_download_ranges");

            migrationBuilder.DropTable(
                name: "oem_file_promotions");

            migrationBuilder.DropTable(
                name: "oem_file_scan_jobs");

            migrationBuilder.DropTable(
                name: "oem_flow_tasks");

            migrationBuilder.DropTable(
                name: "oem_flow_template_node_users");

            migrationBuilder.DropTable(
                name: "oem_flow_template_scopes");

            migrationBuilder.DropTable(
                name: "oem_refresh_tokens");

            migrationBuilder.DropTable(
                name: "oem_upload_sessions");

            migrationBuilder.DropTable(
                name: "oem_download_sessions");

            migrationBuilder.DropTable(
                name: "oem_flow_instance_nodes");

            migrationBuilder.DropTable(
                name: "oem_flow_template_nodes");

            migrationBuilder.DropTable(
                name: "oem_transfer_files");

            migrationBuilder.DropTable(
                name: "oem_flow_instances");

            migrationBuilder.DropTable(
                name: "oem_flow_templates");

            migrationBuilder.DropTable(
                name: "oem_transfers");

            migrationBuilder.DropTable(
                name: "oem_accounts");

            migrationBuilder.DropTable(
                name: "oem_retention_templates");

            migrationBuilder.DropTable(
                name: "oem_companies");

            migrationBuilder.DropIndex(
                name: "idx_outbox_oem_transfer",
                table: "email_outbox");

            migrationBuilder.DropIndex(
                name: "idx_audit_actor_realm",
                table: "audit_logs");

            migrationBuilder.DropColumn(
                name: "oem_transfer_id",
                table: "email_outbox");

            migrationBuilder.DropColumn(
                name: "recipient_account_id",
                table: "email_outbox");

            migrationBuilder.DropColumn(
                name: "recipient_realm",
                table: "email_outbox");

            migrationBuilder.DropColumn(
                name: "leader_account_id",
                table: "departments");

            migrationBuilder.DropColumn(
                name: "actor_account_id",
                table: "audit_logs");

            migrationBuilder.DropColumn(
                name: "actor_realm",
                table: "audit_logs");
        }
    }
}

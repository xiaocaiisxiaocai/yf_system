using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MergeOemLeaderApproverSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Approval templates keep a single "superior leader" node: the first enabled
            // section/department/division leader node of each template (or its first such node
            // when none is enabled) becomes DIRECT_LEADER; the other leader nodes are removed
            // together with their fallback approvers. Started approvals keep their frozen plan.
            migrationBuilder.Sql("""
                UPDATE oem_flow_templates t
                JOIN (SELECT DISTINCT template_id FROM oem_flow_template_nodes
                      WHERE approver_source IN ('SECTION_LEADER','DEPARTMENT_LEADER','DIVISION_LEADER')) changed
                  ON changed.template_id = t.id
                SET t.concurrency_version = t.concurrency_version + 1, t.updated_at = UTC_TIMESTAMP(3);
                """);
            migrationBuilder.Sql("""
                UPDATE oem_flow_template_nodes n
                JOIN (SELECT template_id,
                             COALESCE(MIN(CASE WHEN enabled = 1 THEN sort_no END), MIN(sort_no)) AS keep_sort
                      FROM oem_flow_template_nodes
                      WHERE approver_source IN ('SECTION_LEADER','DEPARTMENT_LEADER','DIVISION_LEADER')
                      GROUP BY template_id) kept
                  ON kept.template_id = n.template_id AND kept.keep_sort = n.sort_no
                SET n.approver_source = 'DIRECT_LEADER',
                    n.name = CASE WHEN n.name IN ('课别主管审批','部门主管审批','事业部主管审批') THEN '上级主管审批' ELSE n.name END;
                """);
            migrationBuilder.Sql("""
                DELETE FROM oem_flow_template_nodes
                WHERE approver_source IN ('SECTION_LEADER','DEPARTMENT_LEADER','DIVISION_LEADER');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only and lossy: removed leader nodes cannot be reconstructed, so Down keeps the merged nodes.
        }
    }
}

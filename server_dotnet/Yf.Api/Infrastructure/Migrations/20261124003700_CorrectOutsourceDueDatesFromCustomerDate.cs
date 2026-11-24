using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <summary>
    /// 原始交期 comes from the IPMS 客户交期 (<c>customerEndDate</c>), not 原交期 (<c>deliveryDate</c>) (外协清单契约, 2026-11-24).
    /// Existing items are corrected once from the mirror: the original due date always, the latest due date only while
    /// purchasing has not changed it (it still equals the old original). Items without a mirrored date are left alone.
    /// Data only; Down keeps the corrected values.
    /// </summary>
    public partial class CorrectOutsourceDueDatesFromCustomerDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // MySQL applies SET assignments left to right, so latest_due_date is compared with the old original first.
            // Strict mode turns a failed date conversion into an error, so only text that already looks like a date is cast
            // (CASE never evaluates the CAST for anything else; a JSON null reads as the text 'null').
            migrationBuilder.Sql("""
                UPDATE outsource_items AS i
                JOIN (
                    SELECT work_order_no,
                           CASE WHEN JSON_UNQUOTE(JSON_EXTRACT(raw_json, '$.customerEndDate')) REGEXP '^[0-9]{4}-[0-9]{2}-[0-9]{2}'
                                THEN CAST(LEFT(JSON_UNQUOTE(JSON_EXTRACT(raw_json, '$.customerEndDate')), 10) AS DATE) END AS due
                    FROM mes_work_orders
                ) AS m ON m.work_order_no = i.work_order_no
                SET i.latest_due_date = CASE WHEN i.latest_due_date <=> i.original_due_date THEN m.due ELSE i.latest_due_date END,
                    i.original_due_date = m.due
                WHERE m.due IS NOT NULL AND NOT (i.original_due_date <=> m.due);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}

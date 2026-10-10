using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daedalus.Infrastructure.Migrations
{
    /// <summary>
    ///     Phase 2.8: at most one <c>node-usage</c> record per (run, seq), enforced by the database. The partial filter
    ///     leaves every other kind free to share a sequence number, as they do today.
    /// </summary>
    public partial class AddNodeUsageUniqueIndex : Migration
    {
        private static readonly string[] _runSeqColumns = ["RunId", "Seq"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A database that already holds two node-usage rows for one (run, seq) would refuse the index. Keep the
            // lowest Id, the first write. This is the one place a record is ever deleted, and only a duplicate of a kept one.
            migrationBuilder.Sql(
                """
                DELETE FROM "WorkflowRunRecords" d
                USING "WorkflowRunRecords" k
                WHERE d."Kind" = 'node-usage' AND k."Kind" = 'node-usage'
                  AND d."RunId" = k."RunId" AND d."Seq" = k."Seq" AND d."Id" > k."Id"
                """);

            migrationBuilder.CreateIndex(
                name: "UX_WorkflowRunRecords_NodeUsage_RunId_Seq",
                table: "WorkflowRunRecords",
                columns: _runSeqColumns,
                unique: true,
                filter: "\"Kind\" = 'node-usage'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_WorkflowRunRecords_NodeUsage_RunId_Seq",
                table: "WorkflowRunRecords");
        }
    }
}

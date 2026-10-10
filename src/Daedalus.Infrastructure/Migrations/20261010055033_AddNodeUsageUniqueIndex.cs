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

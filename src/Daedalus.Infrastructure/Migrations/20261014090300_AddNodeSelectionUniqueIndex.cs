using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daedalus.Infrastructure.Migrations
{
    /// <summary>
    ///     Phase 2.10: at most one <c>node-selection</c> record per (run, seq), so a node visit is decided once and a
    ///     redelivery reads that decision back. No existing row has this kind, so nothing needs reducing first.
    /// </summary>
    public partial class AddNodeSelectionUniqueIndex : Migration
    {
        private static readonly string[] _runSeqColumns = ["RunId", "Seq"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_WorkflowRunRecords_NodeSelection_RunId_Seq",
                table: "WorkflowRunRecords",
                columns: _runSeqColumns,
                unique: true,
                filter: "\"Kind\" = 'node-selection'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_WorkflowRunRecords_NodeSelection_RunId_Seq",
                table: "WorkflowRunRecords");
        }
    }
}

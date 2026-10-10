using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daedalus.Infrastructure.Migrations
{
    /// <summary>
    ///     Phase 2.8: <c>Tasks.WorkflowRunId</c>, the manufacture run a task most recently started, named by value only
    ///     because <c>workflow_run</c> belongs to the Thalos workflow store. The check constraint keeps the derived
    ///     statuses 5 and 6 out of storage. The <c>node-usage</c> backfill is not here: on a fresh database the Thalos
    ///     event table does not exist yet when EF migrations run (analysis R1), so it is a host startup step instead.
    /// </summary>
    public partial class AddTaskWorkflowRunId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "WorkflowRunId",
                table: "Tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tasks_Status_Stored",
                table: "Tasks",
                sql: "\"Status\" BETWEEN 0 AND 4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Tasks_Status_Stored",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "WorkflowRunId",
                table: "Tasks");
        }
    }
}

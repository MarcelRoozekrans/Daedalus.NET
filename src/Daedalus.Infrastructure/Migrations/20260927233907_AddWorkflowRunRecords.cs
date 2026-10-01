using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Daedalus.Infrastructure.Migrations
{
    /// <summary>
    ///     Adds <c>WorkflowRunRecords</c>, the host's insert-only log of what it observed during a workflow run:
    ///     allowed workspace writes and review lens evidence. <c>RunId</c> names a Thalos <c>workflow_run</c> row by
    ///     value only; that table has its own schema and migrations, so there is no foreign key. The index on
    ///     <c>(RunId, Kind, Seq)</c> serves both a run's whole record and one kind of it, in <c>Seq</c> order.
    /// </summary>
    public partial class AddWorkflowRunRecords : Migration
    {
        private static readonly string[] _runKindSeqColumns = ["RunId", "Kind", "Seq"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkflowRunRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    Node = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PrincipalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    StartedById = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowRunRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRunRecords_RunId_Kind_Seq",
                table: "WorkflowRunRecords",
                columns: _runKindSeqColumns);
        }

        /// <summary>
        ///     Drops <c>WorkflowRunRecords</c> and its index. Every record in it is lost: the table is the only copy of
        ///     the write audit and the review evidence, so a rollback past this migration erases what those runs did.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowRunRecords");
        }
    }
}

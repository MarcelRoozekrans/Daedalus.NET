using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daedalus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBrainstormSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BrainstormSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Phase = table.Column<int>(type: "integer", nullable: false),
                    DesignDocument = table.Column<string>(type: "text", nullable: true),
                    ImplementationPlan = table.Column<string>(type: "text", nullable: true),
                    PhaseCompleteSignaled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrainstormSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BrainstormMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BrainstormSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Phase = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrainstormMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BrainstormMessages_BrainstormSessions_BrainstormSessionId",
                        column: x => x.BrainstormSessionId,
                        principalTable: "BrainstormSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BrainstormMessage_SessionId",
                table: "BrainstormMessages",
                column: "BrainstormSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_BrainstormSession_Phase",
                table: "BrainstormSessions",
                column: "Phase");

            migrationBuilder.CreateIndex(
                name: "IX_BrainstormSession_ProjectId",
                table: "BrainstormSessions",
                column: "ProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BrainstormMessages");

            migrationBuilder.DropTable(
                name: "BrainstormSessions");
        }
    }
}

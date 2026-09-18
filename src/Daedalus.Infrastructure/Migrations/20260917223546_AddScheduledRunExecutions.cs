#pragma warning disable CA1861 // generated migration: composite-index column arrays run once at migration time

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daedalus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledRunExecutions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScheduledRunExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurrenceAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Step = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Findings = table.Column<string>(type: "text", nullable: true),
                    Digest = table.Column<string>(type: "text", nullable: true),
                    ChannelId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConversationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PrincipalId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Roles = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledRunExecutions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledRunExecution_Schedule_Occurrence",
                table: "ScheduledRunExecutions",
                columns: new[] { "ScheduleId", "OccurrenceAt" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScheduledRunExecutions");
        }
    }
}

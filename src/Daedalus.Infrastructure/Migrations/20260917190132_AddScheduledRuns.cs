#pragma warning disable CA1861 // generated migration: composite-index column arrays run once at migration time

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daedalus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScheduledRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Cron = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Trigger = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ChannelId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConversationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PrincipalId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Roles = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Origin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    NextRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    MissedOccurrences = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledRun_Enabled_NextRunAt",
                table: "ScheduledRuns",
                columns: new[] { "Enabled", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledRun_Name",
                table: "ScheduledRuns",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScheduledRuns");
        }
    }
}

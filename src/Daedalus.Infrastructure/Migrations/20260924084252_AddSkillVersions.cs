using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daedalus.Infrastructure.Migrations
{
    /// <summary>
    ///     Creates <c>SkillVersions</c>, an insert-only history of every distinct skill content hash ever synced
    ///     into <c>Skills</c>: one row per (Name, ContentHash) pair, so a workflow run pinned to a specific hash
    ///     can load the exact body it started with, even after a later sync replaces the current <c>Skills</c>
    ///     row or deactivates it. Also backfills a version for every <c>Skills</c> row that already exists, so a
    ///     run pinned right after this upgrade can still resolve today's skills by hash.
    /// </summary>
    public partial class AddSkillVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SkillVersions",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    SourcePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkillVersions", x => new { x.Name, x.ContentHash });
                });

            // Backfill: every skill that already exists gets a matching version row, otherwise a run pinned right
            // after this upgrade cannot resolve today's skills by hash. Ordinary syncs append further versions
            // from here on through PostgresSkillStore.UpsertAsync.
            migrationBuilder.Sql("""
                INSERT INTO "SkillVersions" ("Name", "ContentHash", "Description", "Body", "Tags", "SourcePath", "CreatedAt")
                SELECT "Name", "ContentHash", "Description", "Body", "Tags", "SourcePath", "UpdatedAt" FROM "Skills"
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <summary>
        ///     Drops <c>SkillVersions</c>. Destructive in a way <c>Skills</c> itself is not: the version history —
        ///     every hash a workflow run may still be pinned to — is lost, and a run pinned to a version other
        ///     than the current one can no longer resolve it.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SkillVersions");
        }
    }
}

using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daedalus.Infrastructure.Migrations
{
    /// <summary>
    ///     Creates <c>RoleCharterVersions</c>, an insert-only history of every distinct role-charter content hash
    ///     ever synced from <c>roles/</c>, and <c>RoleCharters</c>, a thin pointer (Role, CurrentHash, IsActive,
    ///     UpdatedAt) naming each role's current version instead of duplicating its content a second time. Both
    ///     tables are brand new — phase 2.3's <c>implementer</c>/<c>reviewer</c> agents were fully declared in
    ///     <c>Thalos:Agents</c> until this migration's companion change moved their prose, model and skills to
    ///     versioned charter files, so there is nothing to backfill.
    /// </summary>
    public partial class AddRoleCharters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RoleCharters",
                columns: table => new
                {
                    Role = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CurrentHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleCharters", x => x.Role);
                });

            migrationBuilder.CreateTable(
                name: "RoleCharterVersions",
                columns: table => new
                {
                    Role = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Instructions = table.Column<string>(type: "text", nullable: false),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SourcePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Skills = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleCharterVersions", x => new { x.Role, x.ContentHash });
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoleCharterHead_IsActive",
                table: "RoleCharters",
                column: "IsActive");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoleCharters");

            migrationBuilder.DropTable(
                name: "RoleCharterVersions");
        }
    }
}

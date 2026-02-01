using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daedalus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositoryConfigurations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                table: "CodeAnalysisRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "CodeAnalysisRequests",
                type: "bytea",
                rowVersion: true,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RepositoryConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Platform = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DefaultBranch = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AuthenticationMethod = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CredentialIdentifier = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryConfigurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StructuredLearnings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    Pattern = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Resolution = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    SourceTaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    HitCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastReferencedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StructuredLearnings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryConfiguration_CreatedAt",
                table: "RepositoryConfigurations",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryConfiguration_IsActive",
                table: "RepositoryConfigurations",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryConfiguration_Name",
                table: "RepositoryConfigurations",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_StructuredLearning_Category",
                table: "StructuredLearnings",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_StructuredLearning_CreatedAt",
                table: "StructuredLearnings",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_StructuredLearning_HitCount",
                table: "StructuredLearnings",
                column: "HitCount");

            migrationBuilder.CreateIndex(
                name: "IX_StructuredLearning_ProjectId",
                table: "StructuredLearnings",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_StructuredLearning_Severity",
                table: "StructuredLearnings",
                column: "Severity");

            migrationBuilder.CreateIndex(
                name: "IX_StructuredLearning_SourceTaskId",
                table: "StructuredLearnings",
                column: "SourceTaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepositoryConfigurations");

            migrationBuilder.DropTable(
                name: "StructuredLearnings");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "CodeAnalysisRequests");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "CodeAnalysisRequests");
        }
    }
}

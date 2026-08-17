using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daedalus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTokenUsageColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InputTokens",
                table: "TaskExecutions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ModelId",
                table: "TaskExecutions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutputTokens",
                table: "TaskExecutions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "InputTokens",
                table: "AnalysisIterations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ModelId",
                table: "AnalysisIterations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutputTokens",
                table: "AnalysisIterations",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InputTokens",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "ModelId",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                table: "TaskExecutions");

            migrationBuilder.DropColumn(
                name: "InputTokens",
                table: "AnalysisIterations");

            migrationBuilder.DropColumn(
                name: "ModelId",
                table: "AnalysisIterations");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                table: "AnalysisIterations");
        }
    }
}

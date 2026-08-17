using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daedalus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectRepositoryUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultBranch",
                table: "Projects",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "main");

            migrationBuilder.AddColumn<string>(
                name: "RepositoryUrl",
                table: "Projects",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultBranch",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "RepositoryUrl",
                table: "Projects");
        }
    }
}

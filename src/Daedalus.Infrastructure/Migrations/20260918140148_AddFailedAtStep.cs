using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daedalus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFailedAtStep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FailedAtStep",
                table: "ScheduledRunExecutions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailedAtStep",
                table: "ScheduledRunExecutions");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daedalus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSemanticEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS vector;");

            migrationBuilder.AddColumn<float[]>(
                name: "Embedding",
                table: "StructuredLearnings",
                type: "vector(384)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Embedding",
                table: "StructuredLearnings");
        }
    }
}

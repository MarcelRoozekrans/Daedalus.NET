using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daedalus.Infrastructure.Migrations
{
    /// <summary>
    ///     Adds an HNSW vector index on the Embedding column for efficient cosine similarity search.
    ///     HNSW is preferred over IVFFlat because it does not require periodic reindexing
    ///     and provides better query performance at slightly higher write cost.
    /// </summary>
    public partial class AddEmbeddingHnswIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Note: CONCURRENTLY is omitted because EF Core migrations run within a transaction
            // and PostgreSQL does not allow CREATE INDEX CONCURRENTLY inside a transaction.
            // For production deployments on large tables, consider running this manually with CONCURRENTLY.
            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "IX_StructuredLearnings_Embedding"
                ON "StructuredLearnings"
                USING hnsw ("Embedding" vector_cosine_ops);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS "IX_StructuredLearnings_Embedding";
                """);
        }
    }
}

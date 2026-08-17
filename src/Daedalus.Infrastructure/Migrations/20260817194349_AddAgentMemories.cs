#pragma warning disable CA1861 // generated migration: composite-index column arrays run once at migration time

using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daedalus.Infrastructure.Migrations
{
    /// <summary>
    ///     Creates <c>AgentMemories</c> (the Thalos memory store), copies the Ralph <c>StructuredLearnings</c> rows into it and
    ///     drops the old table. Vectors are not copied — they are rebuilt from the text by
    ///     <c>ReindexPendingMemoriesHostedService</c>, which is why every migrated row is written <c>IndexPending = true</c>.
    /// </summary>
    public partial class AddAgentMemories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentMemories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Importance = table.Column<double>(type: "double precision", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastRecalledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RecallCount = table.Column<int>(type: "integer", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    IndexPending = table.Column<bool>(type: "boolean", nullable: false),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentMemories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentMemory_CreatedAt_Id",
                table: "AgentMemories",
                columns: new[] { "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentMemory_IndexPending",
                table: "AgentMemories",
                column: "IndexPending");

            migrationBuilder.CreateIndex(
                name: "IX_AgentMemory_IsArchived",
                table: "AgentMemories",
                column: "IsArchived");

            migrationBuilder.CreateIndex(
                name: "IX_AgentMemory_Owner_Agent",
                table: "AgentMemories",
                columns: new[] { "OwnerId", "AgentId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentMemory_Owner_Kind",
                table: "AgentMemories",
                columns: new[] { "OwnerId", "Kind" });

            // Copy the Ralph learnings into the memory table (owner "daedalus", kind "learning"); vectors are rebuilt by
            // ReindexPendingMemoriesHostedService (IndexPending = true). Mirrors LearningMemoryMapping (text, tags, importance, source).
            migrationBuilder.Sql("""
                INSERT INTO "AgentMemories"
                    ("Id","OwnerId","AgentId","Kind","Text","Tags","Source","Importance","CreatedAt","UpdatedAt","LastRecalledAt","RecallCount","IsArchived","IndexPending")
                SELECT
                    s."Id",
                    'daedalus',
                    NULL,
                    'learning',
                    left(CASE WHEN s."Pattern" = s."Resolution" THEN s."Pattern" ELSE s."Pattern" || E'\n' || s."Resolution" END, 4000),
                    ARRAY[
                        CASE s."Category" WHEN 0 THEN 'errorpattern' WHEN 1 THEN 'successpattern' WHEN 2 THEN 'codeconvention' WHEN 3 THEN 'dependencyinfo' WHEN 4 THEN 'architecturedecision' ELSE 'general' END,
                        CASE s."Severity" WHEN 0 THEN 'low' WHEN 1 THEN 'medium' WHEN 2 THEN 'high' WHEN 3 THEN 'critical' ELSE 'medium' END
                    ] || COALESCE((SELECT array_agg(lower(u.t) ORDER BY u.ord) FROM unnest(s."Tags") WITH ORDINALITY AS u(t, ord) WHERE u.ord <= 8), '{}'::text[]),
                    CASE WHEN s."SourceTaskId" IS NULL THEN 'migration' ELSE 'ralph:task/' || s."SourceTaskId"::text END,
                    CASE s."Severity" WHEN 3 THEN 1.0 WHEN 2 THEN 0.8 WHEN 1 THEN 0.5 ELSE 0.3 END,
                    s."CreatedAt",
                    s."CreatedAt",
                    s."LastReferencedAt",
                    s."HitCount",
                    false,
                    true
                FROM "StructuredLearnings" s;
                """);

            // The HNSW index was created by raw SQL (AddEmbeddingHnswIndex), so EF does not know about it.
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_StructuredLearnings_Embedding";""");

            migrationBuilder.DropTable(
                name: "StructuredLearnings");
        }

        /// <summary>
        ///     Recreates the empty <c>StructuredLearnings</c> table (without the <c>vector(384)</c> Embedding column, whose
        ///     mapping left the model with Pgvector.EntityFrameworkCore) and drops <c>AgentMemories</c>. The learnings rows
        ///     themselves are <b>not</b> restored — the copy above is one-way. The <c>vector</c> extension stays: Rag.NET's
        ///     <c>rag_chunks</c> needs it.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentMemories");

            migrationBuilder.CreateTable(
                name: "StructuredLearnings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HitCount = table.Column<int>(type: "integer", nullable: false),
                    LastReferencedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Pattern = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Resolution = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    SourceTaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StructuredLearnings", x => x.Id);
                });

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
    }
}

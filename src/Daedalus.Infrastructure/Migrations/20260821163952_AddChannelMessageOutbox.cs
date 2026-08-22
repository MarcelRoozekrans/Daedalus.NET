#pragma warning disable CA1861 // generated migration: composite-index column arrays run once at migration time

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daedalus.Infrastructure.Migrations
{
    /// <summary>
    ///     Creates <c>OutboxMessages</c>: ZeroAlloc.Outbox's durable-delivery table, mapped by
    ///     <c>OutboxDbContextExtensions.AddOutboxMessages</c>. One row per queued <c>ChannelMessageQueued</c> (or any
    ///     future <c>[OutboxMessage]</c> type); the index on <c>(Status, NextRetryAt)</c> is what the poller's
    ///     "fetch pending, due" query hits.
    /// </summary>
    public partial class AddChannelMessageOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TypeName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    NextRetryAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeadLetterError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Status_NextRetryAt",
                table: "OutboxMessages",
                columns: new[] { "Status", "NextRetryAt" });
        }

        /// <summary>
        ///     Drops <c>OutboxMessages</c>. Destructive: every message still <c>Pending</c>, mid-retry or
        ///     dead-lettered is lost — any chat reply queued but not yet delivered will never be sent.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutboxMessages");
        }
    }
}

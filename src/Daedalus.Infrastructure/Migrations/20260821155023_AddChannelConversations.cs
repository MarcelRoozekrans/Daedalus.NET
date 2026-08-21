#pragma warning disable CA1861 // generated migration: composite-index column arrays run once at migration time

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daedalus.Infrastructure.Migrations
{
    /// <summary>
    ///     Creates <c>ChannelConversations</c>: one row per external chat (Telegram chat, the CLI's "console"
    ///     conversation, …), binding it to the Thalos session currently serving it. The unique index on
    ///     (ChannelId, ConversationId) is the natural key, enforced by the database.
    /// </summary>
    public partial class AddChannelConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChannelConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConversationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastActivityAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelConversations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelConversation_Channel_Conversation",
                table: "ChannelConversations",
                columns: new[] { "ChannelId", "ConversationId" },
                unique: true);
        }

        /// <summary>
        ///     Drops <c>ChannelConversations</c>. Destructive: every channel's memory of which session is currently
        ///     serving which chat is lost, so the next inbound message on any channel starts a fresh session.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelConversations");
        }
    }
}

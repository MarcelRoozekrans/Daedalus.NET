using System.Text.Json;
using Daedalus.Agents.Sessions;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Thalos;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Agents;

/// <summary>
///     The bytes <see cref="PostgresAgentSessionStore"/> writes into <c>AgentMessages.ContentJson</c>, captured at the
///     serializer from today's reflective <c>Serialize(ChatMessage)</c> before phase 3.2 reads M.E.AI's generated metadata
///     instead, and a read of a row as Postgres hands it back. The column is <c>jsonb</c>, which reorders keys, so bytes
///     are pinned at the serializer and the row is pinned by what it reads back as.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AgentSessionGoldenTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string Awkward = "<a & 'é' 🚀>";

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    ///     A user's text, an assistant's text with a tool call, and the tool's result. Red: build
    ///     <c>AgentSessionJson</c>'s options from <c>JsonSerializerOptions.Default</c> instead of
    ///     <c>AIJsonUtilities.DefaultOptions</c>; the content loses its <c>$type</c> discriminators and the property names
    ///     their camelCase.
    /// </summary>
    [Fact]
    public void A_conversation_is_written_as_captured()
    {
        var written = string.Join('\n', Conversation().Select(AgentSessionJson.Serialize));

        written.Should().Be(ExpectedBytes);
    }

    /// <summary>
    ///     Rows as Postgres returns them, <c>$type</c> no longer first, read back through the store. Red: drop
    ///     <c>AllowOutOfOrderMetadataProperties = true</c> from <c>AgentSessionJson</c>'s options; the read throws on
    ///     the late discriminator.
    /// </summary>
    [Fact]
    public async Task Stored_rows_read_back_as_the_conversation()
    {
        var store = new PostgresAgentSessionStore(new DbContextFactory(fixture.ConnectionString), new FakeTimeProvider());
        var session = await store.CreateAsync(new AgentId(Guid.NewGuid()), "owner-1", CancellationToken.None);
        session.IsSuccess.Should().BeTrue();

        var at = new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);
        await using (var db = new ApplicationDbContext(PostgresFixture.CreateDbContextOptions(fixture.ConnectionString)))
        {
            var sequence = 0;
            foreach (var (role, json) in StoredRows)
            {
                db.AgentMessages.Add(AgentMessage.Create(session.Value.Id.Value, sequence++, role, json, null, null, null, at).Value);
            }

            await db.SaveChangesAsync();
        }

        var loaded = await store.LoadMessagesAsync(session.Value.Id, CancellationToken.None);

        loaded.IsSuccess.Should().BeTrue();
        var messages = loaded.Value;
        messages.Should().HaveCount(3);

        messages[0].Role.Should().Be(ChatRole.User);
        messages[0].Text.Should().Be(Awkward);

        messages[1].Role.Should().Be(ChatRole.Assistant);
        messages[1].Text.Should().Be("Writing it.");
        var call = messages[1].Contents.OfType<FunctionCallContent>().Should().ContainSingle().Which;
        call.CallId.Should().Be("call-1");
        call.Name.Should().Be("workspace__write_file");
        call.Arguments.Should().NotBeNull();
        call.Arguments!.Keys.Should().Equal("path", "count");
        ((JsonElement)call.Arguments["path"]!).GetString().Should().Be(Awkward);
        ((JsonElement)call.Arguments["count"]!).GetInt32().Should().Be(3);

        messages[2].Role.Should().Be(ChatRole.Tool);
        var result = messages[2].Contents.OfType<FunctionResultContent>().Should().ContainSingle().Which;
        result.CallId.Should().Be("call-1");
        ((JsonElement)result.Result!).GetString().Should().Be(Awkward);
    }

    private static List<ChatMessage> Conversation() =>
    [
        new(ChatRole.User, Awkward),
        new(ChatRole.Assistant,
        [
            new TextContent("Writing it."),
            new FunctionCallContent("call-1", "workspace__write_file",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["path"] = Awkward, ["count"] = 3 }),
        ]),
        new(ChatRole.Tool, [new FunctionResultContent("call-1", Awkward)]),
    ];

    private const string ExpectedBytes = """
        {
          "role": "user",
          "contents": [
            {
              "$type": "text",
              "text": "<a & 'é' \uD83D\uDE80>"
            }
          ]
        }
        {
          "role": "assistant",
          "contents": [
            {
              "$type": "text",
              "text": "Writing it."
            },
            {
              "$type": "functionCall",
              "name": "workspace__write_file",
              "arguments": {
                "path": "<a & 'é' \uD83D\uDE80>",
                "count": 3
              },
              "informationalOnly": false,
              "callId": "call-1"
            }
          ]
        }
        {
          "role": "tool",
          "contents": [
            {
              "$type": "functionResult",
              "result": "<a & 'é' \uD83D\uDE80>",
              "callId": "call-1"
            }
          ]
        }
        """;

    /// <summary>The three messages as Postgres hands back the rows the store wrote, captured from a real write.</summary>
    private static readonly (string Role, string Json)[] StoredRows =
    [
        ("user", """{"role": "user", "contents": [{"text": "<a & 'é' 🚀>", "$type": "text"}]}"""),
        ("assistant", """{"role": "assistant", "contents": [{"text": "Writing it.", "$type": "text"}, {"name": "workspace__write_file", "$type": "functionCall", "callId": "call-1", "arguments": {"path": "<a & 'é' 🚀>", "count": 3}, "informationalOnly": false}]}"""),
        ("tool", """{"role": "tool", "contents": [{"$type": "functionResult", "callId": "call-1", "result": "<a & 'é' 🚀>"}]}"""),
    ];

    private sealed class DbContextFactory(string connectionString) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(PostgresFixture.CreateDbContextOptions(connectionString));
    }
}

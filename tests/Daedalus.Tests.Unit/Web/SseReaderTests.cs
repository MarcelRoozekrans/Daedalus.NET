using System.Text;
using Daedalus.Web.Services;

namespace Daedalus.Tests.Unit.Web;

public sealed class SseReaderTests
{
    [Fact]
    public async Task Yields_one_tuple_per_block_with_event_names()
    {
        var blocks = await ReadAllAsync("event: text-delta\ndata: {\"kind\":\"text-delta\"}\n\nevent: done\ndata: {}\n\n");

        blocks.Should().Equal(
            ("text-delta", "{\"kind\":\"text-delta\"}"),
            ("done", "{}"));
    }

    [Fact]
    public async Task Multi_line_data_is_joined_with_newline()
    {
        var blocks = await ReadAllAsync("event: text-delta\ndata: line one\ndata: line two\ndata:\n\n");

        blocks.Should().Equal(("text-delta", "line one\nline two\n"));
    }

    [Fact]
    public async Task Comment_lines_and_unknown_fields_are_ignored()
    {
        var blocks = await ReadAllAsync(": keep-alive\nid: 7\nretry: 1000\nevent: usage\ndata: {\"a\":1}\n\n: another comment\n\n");

        blocks.Should().Equal(("usage", "{\"a\":1}"));
    }

    [Fact]
    public async Task Final_block_without_trailing_blank_line_is_still_yielded()
    {
        var blocks = await ReadAllAsync("event: text-delta\ndata: a\n\nevent: error\ndata: {\"kind\":\"error\"}");

        blocks.Should().Equal(
            ("text-delta", "a"),
            ("error", "{\"kind\":\"error\"}"));
    }

    [Fact]
    public async Task Block_without_data_is_dropped_and_resets_event_name()
    {
        var blocks = await ReadAllAsync("event: ping\n\ndata: payload\n\n");

        blocks.Should().Equal(((string?)null, "payload"));
    }

    [Fact]
    public async Task Crlf_line_endings_and_missing_space_after_colon_are_accepted()
    {
        var blocks = await ReadAllAsync("event:done\r\ndata:{}\r\n\r\n");

        blocks.Should().Equal(("done", "{}"));
    }

    [Fact]
    public async Task Only_one_leading_space_is_stripped_from_data()
    {
        var blocks = await ReadAllAsync("data:  two spaces\n\n");

        blocks.Should().Equal(((string?)null, " two spaces"));
    }

    [Fact]
    public async Task Cancellation_stops_reading()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: a\n\n"));

        var act = async () => await SseReader.ReadAsync(stream, cts.Token).ToListAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async Task<List<(string? Event, string Data)>> ReadAllAsync(string payload)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var blocks = new List<(string? Event, string Data)>();
        await foreach (var block in SseReader.ReadAsync(stream))
        {
            blocks.Add(block);
        }

        return blocks;
    }
}

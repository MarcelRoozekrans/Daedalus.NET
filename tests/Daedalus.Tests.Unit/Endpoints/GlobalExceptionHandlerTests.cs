using System.Text.Json;
using Daedalus.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.ORM;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     A guarded write that loses its race and is not caught by its store reaches the handler as a
///     <see cref="ZeroAllocOrmConcurrencyException"/>, as EF Core's <c>DbUpdateConcurrencyException</c> did before
///     phase 3.5, and keeps the same 409 answer.
/// </summary>
public sealed class GlobalExceptionHandlerTests
{
    /// <summary>
    ///     Red: map <c>ZeroAllocOrmConcurrencyException</c> back to anything else in the handler's switch, or drop the
    ///     arm; the status is 500.
    /// </summary>
    [Fact]
    public async Task A_lost_concurrency_race_answers_409()
    {
        var (status, problem) = await HandleAsync(new ZeroAllocOrmConcurrencyException("UpdateRow affected no rows"));

        status.Should().Be(StatusCodes.Status409Conflict);
        problem.GetProperty("title").GetString().Should().Be("Concurrency conflict");
    }

    /// <summary>Red: send every exception to the concurrency arm; this one answers 409.</summary>
    [Fact]
    public async Task Any_other_exception_answers_500()
    {
        var (status, _) = await HandleAsync(new InvalidOperationException("boom"));

        status.Should().Be(StatusCodes.Status500InternalServerError);
    }

    private static async Task<(int Status, JsonElement Problem)> HandleAsync(Exception exception)
    {
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

        (await handler.TryHandleAsync(context, exception, CancellationToken.None)).Should().BeTrue();

        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return (context.Response.StatusCode, document.RootElement.Clone());
    }
}

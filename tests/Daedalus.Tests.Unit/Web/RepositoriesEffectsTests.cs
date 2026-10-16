using System.Net;
using System.Text.Json;
using Daedalus.Application.DTOs;
using Daedalus.Web.Services;
using Daedalus.Web.State.Repositories;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Flux;
using ZeroAlloc.Flux.Generated;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     The repositories effects: the page's read, a refusal that is a state, failures that are the error, the writes'
///     outcomes, and what they never store: a session being renewed, or a read whose page went.
/// </summary>
public sealed class RepositoriesEffectsTests : IDisposable
{
    private const string Path = "/api/repositories";
    private const string ServerError = "The server answered 500 Internal Server Error.";

    private static readonly Guid Id = Guid.NewGuid();

    private readonly RecordingHandler _handler = new();
    private readonly ServiceProvider _provider = new ServiceCollection().AddZeroAllocFlux().BuildServiceProvider();
    private readonly IStore<RepositoriesState> _store;
    private readonly RepositoriesEffects _effects;
    private readonly RecordingLogger<RepositoriesEffects> _logger = new();

    public RepositoriesEffectsTests()
    {
        _store = _provider.GetRequiredService<IStore<RepositoriesState>>();
        _effects = new RepositoriesEffects(
            _handler.RepositoriesClient(), _provider.GetRequiredService<IDispatcher>(), _store, _logger);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _handler.Dispose();
    }

    private static string List(params string[] names) =>
        JsonSerializer.Serialize(names.Select(n => new { id = Guid.NewGuid(), name = n }), JsonSerializerOptions.Web);

    private static HttpResponseMessage SigningIn()
    {
        var navigation = new RecordingNavigation();
        throw new AccessTokenNotAvailableException(navigation, navigation.RefreshFailed(), scopes: null);
    }

    /// <summary>
    ///     A read keeps the repositories the API holds.
    ///     Red: dispatch nothing on a success in LoadAsync; Repositories stays null.
    /// </summary>
    [Fact]
    public async Task A_read_keeps_the_repositories()
    {
        _handler.OnJson(Path, List("atlas", "hermes"));

        await _effects.LoadAsync(CancellationToken.None);

        _store.Value.Repositories.Should().HaveCount(2);
        _store.Value.Repositories![0].Name.Should().Be("atlas");
    }

    /// <summary>
    ///     A 403 is the Forbidden state, not an error, and is not logged: a user who is not an admin is no fault.
    ///     Red: log a warning for every read that is not a list; the refusal writes one.
    /// </summary>
    [Fact]
    public async Task A_refused_read_is_the_forbidden_state()
    {
        _handler.On(Path, () => new HttpResponseMessage(HttpStatusCode.Forbidden));

        await _effects.LoadAsync(CancellationToken.None);

        _store.Value.Forbidden.Should().BeTrue();
        _store.Value.Error.Should().BeNull();
        _logger.Entries.Should().BeEmpty("a user who is not an admin is no fault");
    }

    /// <summary>
    ///     A failed read is the error, with the status the server answered, and is logged as a warning.
    ///     Red: dispatch nothing on a failure; the error stays null.
    ///     Red: drop the LogLoadFailed call; no warning is written.
    /// </summary>
    [Fact]
    public async Task A_failed_read_is_the_error_and_logs_a_warning()
    {
        _handler.On(Path, () => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await _effects.LoadAsync(CancellationToken.None);

        _store.Value.Error.Should().Be(ServerError);
        _logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    /// <summary>
    ///     A read that times out while the page is still open is the error: only the page's own cancellation is dropped.
    ///     Red: drop every failure whose text is a cancellation or a timeout; the error stays null.
    /// </summary>
    [Fact]
    public async Task A_timed_out_read_is_the_error()
    {
        _handler.On(Path, HttpResponseMessage () => throw new TaskCanceledException("timeout"));

        await _effects.LoadAsync(CancellationToken.None);

        _store.Value.Error.Should().Be(RepositoriesApiClient.TimedOut);
    }

    /// <summary>
    ///     A read whose token cannot be refreshed stores no error and shows no list, because the browser is on its way to
    ///     sign in and the layout says so; it is not logged.
    ///     Red: dispatch RepositoriesLoadFailed for SigningInAgain too; the error is the signing-in text.
    ///     Red: log the failure before the SigningInAgain check in LoadAsync; the renewal writes a warning.
    ///     Red: drop the RepositoriesLoadStarted dispatch; the list from the read before is still shown.
    /// </summary>
    [Fact]
    public async Task Signing_in_again_stores_no_error()
    {
        _handler.OnJson(Path, List("atlas"));
        await _effects.LoadAsync(CancellationToken.None);
        _handler.On(Path, SigningIn);

        await _effects.LoadAsync(CancellationToken.None);

        _store.Value.Error.Should().BeNull("a session being renewed is not an error to show");
        _store.Value.Repositories.Should().BeNull("the page shows nothing of its own while sign-in is under way");
        _logger.Entries.Should().BeEmpty("a session being renewed is not a failure");
    }

    /// <summary>
    ///     A read cancelled because its page closed stores no error and is not logged.
    ///     Red: drop the cancellation check in LoadAsync; the error is "Request was cancelled".
    ///     Red: log the failure before the cancellation check in LoadAsync; the cancellation writes a warning.
    /// </summary>
    [Fact]
    public async Task A_read_cancelled_by_its_page_stores_no_error()
    {
        using var closing = new CancellationTokenSource();
        _handler.On(Path, async () =>
        {
            await closing.CancelAsync();
            throw new OperationCanceledException(closing.Token);
        });

        await _effects.LoadAsync(closing.Token);

        _store.Value.Error.Should().BeNull();
        _logger.Entries.Should().BeEmpty("a page closing is not a failure");
    }

    /// <summary>
    ///     A read whose page closes as its answer arrives stores nothing. The page closes while the client buffers the
    ///     403's body: the client still returns the Forbidden success, which reads no body, so only the effect's own
    ///     check can drop it.
    ///     Red: drop the cancelled-token check on the success branch in LoadAsync; the late refusal is Forbidden.
    /// </summary>
    [Fact]
    public async Task A_read_answered_after_its_page_closed_stores_nothing()
    {
        using var closing = new CancellationTokenSource();
        _handler.On(Path, () => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new ClosingContent(closing),
        });

        await _effects.LoadAsync(closing.Token);

        _store.Value.Forbidden.Should().BeFalse("the page that asked is gone");
        _store.Value.Error.Should().BeNull();
    }

    /// <summary>
    ///     A read for a page that has already gone, such as the reload after a write that finished once the page was left
    ///     and opened again, dispatches nothing and sends nothing: the reopened page's list and sequence stay, so its own
    ///     answer still lands.
    ///     Red: drop the cancelled-token guard in LoadAsync; the list is cleared and the sequence moves.
    /// </summary>
    [Fact]
    public async Task A_read_for_a_page_already_gone_changes_nothing()
    {
        _handler.OnJson(Path, List("atlas"));
        await _effects.LoadAsync(CancellationToken.None);
        var sequence = _store.Value.Sequence;
        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();

        await _effects.LoadAsync(gone.Token);

        _store.Value.Repositories.Should().ContainSingle();
        _store.Value.Sequence.Should().Be(sequence);
        _handler.Requests.Should().ContainSingle("the page that asked is gone");
    }

    /// <summary>
    ///     Two reads close together, such as Retry clicked twice: the first one's answer lands after the second's and
    ///     does not replace it.
    ///     Red: number every read 1 in LoadAsync; the older list wins.
    /// </summary>
    [Fact]
    public async Task An_older_answer_does_not_replace_a_newer_one()
    {
        var release = new TaskCompletionSource();
        var calls = 0;
        _handler.On(Path, async () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await release.Task;
                return RecordingHandler.Json(HttpStatusCode.OK, List("old"));
            }

            return RecordingHandler.Json(HttpStatusCode.OK, List("new"));
        });

        var first = _effects.LoadAsync(CancellationToken.None);
        await _effects.LoadAsync(CancellationToken.None);
        release.SetResult();
        await first;

        _store.Value.Repositories.Should().ContainSingle().Which.Name.Should().Be("new");
    }

    /// <summary>The write each case runs, and the method and path it must send.</summary>
    public static TheoryData<RepositoryWrite, string, string> Writes => new()
    {
        { RepositoryWrite.Created, "POST", Path },
        { RepositoryWrite.Updated, "PUT", $"{Path}/{Id}" },
        { RepositoryWrite.Deleted, "DELETE", $"{Path}/{Id}" },
        { RepositoryWrite.Tested, "POST", $"{Path}/{Id}/test-connection" },
    };

    private Task<RepositorySave?> WriteAsync(RepositoryWrite write, CancellationToken ct = default) =>
        WriteAsync(write, Id, ct);

    private Task<RepositorySave?> WriteAsync(RepositoryWrite write, Guid id, CancellationToken ct = default) => write switch
    {
        RepositoryWrite.Created => _effects.CreateAsync(new CreateRepositoryConfigurationDto { Name = "atlas" }, ct),
        RepositoryWrite.Updated => _effects.UpdateAsync(id, new UpdateRepositoryConfigurationDto { Name = "atlas" }, ct),
        RepositoryWrite.Deleted => _effects.DeleteAsync(id, ct),
        _ => _effects.TestConnectionAsync(id, ct),
    };

    /// <summary>
    ///     A write the server accepts returns a save of that write, sent to its own endpoint.
    ///     Red: SaveAsync returns null on a save; the returned outcome is missing.
    ///     Red: send UpdateAsync through api.CreateAsync; the PUT is a POST to the list.
    ///     Red: send TestConnectionAsync through api.DeleteAsync; the test is a DELETE.
    ///     Red: return a save of Created for every write; the outcome's write is wrong.
    /// </summary>
    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_saved_write_is_its_outcome(RepositoryWrite write, string method, string path)
    {
        _handler.On(path, () => new HttpResponseMessage(HttpStatusCode.NoContent));

        var outcome = await WriteAsync(write);

        outcome.Should().Be(new RepositorySave(write, null));
        _handler.Requests.Should().ContainSingle().Which.Should().Match<RecordedRequest>(r =>
            r.Method.Method == method && r.PathAndQuery == path);
    }

    /// <summary>
    ///     A create sends the repository it was given.
    ///     Red: send a new CreateRepositoryConfigurationDto in CreateAsync; the name is empty.
    /// </summary>
    [Fact]
    public async Task A_create_sends_the_repository()
    {
        _handler.On(Path, () => new HttpResponseMessage(HttpStatusCode.Created));

        await WriteAsync(RepositoryWrite.Created);

        _handler.Requests.Should().ContainSingle().Which.Body.Should().Contain("\"name\":\"atlas\"");
    }

    /// <summary>
    ///     A write the server refuses returns the refusal with the status it answered, and is logged as a warning.
    ///     Red: SaveAsync returns null on a refusal; the returned outcome is missing.
    ///     Red: return a fixed "Refused." instead of the client's text; the status is lost.
    ///     Red: drop the LogWriteRefused call; no warning is written.
    /// </summary>
    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_refused_write_says_the_status(RepositoryWrite write, string method, string path)
    {
        _ = method;
        _handler.On(path, () => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var outcome = await WriteAsync(write);

        outcome.Should().Be(new RepositorySave(write, ServerError));
        _logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    /// <summary>
    ///     A write that gets no answer returns a refusal with <see cref="RepositoriesApiClient.NoAnswer"/>, which the
    ///     page words itself.
    ///     Red: drop a NoAnswer failure in SaveAsync as if the session were renewed; the outcome is null.
    /// </summary>
    [Fact]
    public async Task A_write_with_no_answer_is_refused()
    {
        _handler.On($"{Path}/{Id}", HttpResponseMessage () => throw new HttpRequestException("unreachable"));

        var outcome = await WriteAsync(RepositoryWrite.Deleted);

        outcome.Should().Be(new RepositorySave(RepositoryWrite.Deleted, RepositoriesApiClient.NoAnswer));
    }

    /// <summary>
    ///     A write whose token cannot be refreshed returns no outcome, so the page shows nothing: the layout says the
    ///     session ended. It is not logged.
    ///     Red: drop the SigningInAgain check in SaveAsync; the outcome is the signing-in text.
    ///     Red: log the refusal before the SigningInAgain check in SaveAsync; the renewal writes a warning.
    /// </summary>
    [Fact]
    public async Task Signing_in_again_during_a_write_is_no_outcome()
    {
        _handler.On($"{Path}/{Id}", SigningIn);

        var outcome = await WriteAsync(RepositoryWrite.Updated);

        outcome.Should().BeNull();
        _logger.Entries.Should().BeEmpty("a session being renewed is not a failure");
    }

    /// <summary>
    ///     A write whose caller cancelled it is no outcome, and is not logged.
    ///     Red: drop the cancellation check in SaveAsync; the outcome is "Request was cancelled".
    /// </summary>
    [Fact]
    public async Task A_write_its_caller_cancelled_is_no_outcome()
    {
        using var closing = new CancellationTokenSource();
        _handler.On($"{Path}/{Id}", async () =>
        {
            await closing.CancelAsync();
            throw new OperationCanceledException(closing.Token);
        });

        var outcome = await WriteAsync(RepositoryWrite.Deleted, closing.Token);

        outcome.Should().BeNull();
        _logger.Entries.Should().BeEmpty("a caller going away is not a failure");
    }

    /// <summary>
    ///     A write leaves the list alone: the page reloads it when the outcome says so.
    ///     Red: dispatch RepositoriesLoadStarted from SaveAsync; the list is cleared.
    /// </summary>
    [Fact]
    public async Task A_write_leaves_the_list_alone()
    {
        _handler.OnJson(Path, List("atlas"));
        await _effects.LoadAsync(CancellationToken.None);
        _handler.On($"{Path}/{Id}", () => new HttpResponseMessage(HttpStatusCode.NoContent));

        await WriteAsync(RepositoryWrite.Deleted);

        _store.Value.Repositories.Should().ContainSingle();
    }

    /// <summary>A body that closes the page while the client buffers it, after the answer has arrived.</summary>
    private sealed class ClosingContent(CancellationTokenSource closing) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            await closing.CancelAsync();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }
    }
}

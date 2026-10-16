using System.Net;
using System.Text.Json;
using Daedalus.Application.DTOs;
using Daedalus.Web.State.Prd;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Flux;
using ZeroAlloc.Flux.Generated;
using Complexity = Daedalus.Domain.Entities.Complexity;
using Priority = Daedalus.Domain.Entities.Priority;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     The PRD effects: generate, convert, a refusal's reason, and what they never store: a session being renewed, or an
///     answer whose page went. A failure is logged with its text, never the requirements.
/// </summary>
public sealed class PrdEffectsTests : IDisposable
{
    private const string GeneratePath = "/api/prd/generate";
    private const string ConvertPath = "/api/prd/convert-to-tasks";
    private const string Requirements = "Let a shop owner count stock from a phone.";
    private const string Context = "Phones only.";
    private const string Refusal = "The model could not be reached.";

    private static readonly Guid ProjectId = Guid.NewGuid();

    private static readonly PrdResponseDto Prd = new(
        "prd-1", ProjectId, "A widget shop.", ["Sell widgets"], [],
        [new PrdItemDto("Cart", "A cart", "Holds widgets", Priority.High, Complexity.Medium)], [], DateTime.UnixEpoch);

    private static readonly PrdItemForConversionDto Item =
        new("Cart", "A cart", "Holds widgets", Priority.High, Complexity.Medium, [], "Implementation", 1);

    private static readonly TaskDto Task = new(
        Guid.NewGuid(), "TASK-1", ProjectId, "Cart", "A cart", 2, "Implementation", 1, [], [], 2, "p", "DONE", 10, 0, null,
        null, 0, DateTime.UnixEpoch, null, null, null, [], null, null, null, null);

    private readonly RecordingHandler _handler = new();
    private readonly ServiceProvider _provider = new ServiceCollection().AddZeroAllocFlux().BuildServiceProvider();
    private readonly IStore<PrdState> _store;
    private readonly IDispatcher _dispatcher;
    private readonly PrdEffects _effects;
    private readonly RecordingLogger<PrdEffects> _logger = new();

    public PrdEffectsTests()
    {
        _store = _provider.GetRequiredService<IStore<PrdState>>();
        _dispatcher = _provider.GetRequiredService<IDispatcher>();
        _effects = new PrdEffects(_handler.Client(), _dispatcher, _store, _logger);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _handler.Dispose();
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);

    private static string RefusalJson => Json(new { error = Refusal });

    private static HttpResponseMessage SigningIn()
    {
        var navigation = new RecordingNavigation();
        throw new AccessTokenNotAvailableException(navigation, navigation.RefreshFailed(), scopes: null);
    }

    private async Task PickProjectAsync() => await _dispatcher.DispatchAsync(new PrdProjectSelected(ProjectId));

    private async Task ReviewAsync()
    {
        await PickProjectAsync();
        _handler.OnJson(GeneratePath, Json(Prd));
        await _effects.GenerateAsync(Requirements, Context, CancellationToken.None);
        _logger.Entries.Clear();
    }

    /// <summary>
    ///     Generate posts the picked project, the requirements and the context, and the PRD shows for review, logged
    ///     without its content.
    ///     Red: dispatch nothing on a success in GenerateAsync; the wizard stays on the requirements.
    ///     Red: post Guid.Empty for the project in GenerateAsync; the posted project is not the picked one.
    ///     Red: post a null context in GenerateAsync; the posted context is missing.
    ///     Red: drop the LogGenerated call; no information entry is written.
    /// </summary>
    [Fact]
    public async Task Generate_posts_the_requirements_and_shows_the_prd()
    {
        await PickProjectAsync();
        _handler.OnJson(GeneratePath, Json(Prd));

        await _effects.GenerateAsync(Requirements, Context, CancellationToken.None);

        _store.Value.Step.Should().Be(PrdStep.PrdReview);
        _store.Value.Prd!.ProductOverview.Should().Be(Prd.ProductOverview);
        var posted = JsonDocument.Parse(_handler.Requests.Single().Body!).RootElement;
        posted.GetProperty("projectId").GetGuid().Should().Be(ProjectId);
        posted.GetProperty("userRequirements").GetString().Should().Be(Requirements);
        posted.GetProperty("context").GetString().Should().Be(Context);
        _logger.Entries.Should().ContainSingle().Which.Should().Match<(LogLevel Level, string Message)>(e =>
            e.Level == LogLevel.Information && !e.Message.Contains(Requirements, StringComparison.Ordinal));
    }

    /// <summary>
    ///     A refused generate is the controller's reason, not its JSON, stays on the requirements, and is logged as a
    ///     warning with the reason and not the requirements. This is R8's refusal half.
    ///     Red: dispatch nothing on a failure in GenerateAsync; the error stays null.
    ///     Red: drop the LogGenerateFailed call; no warning is written.
    ///     Red: log the requirements in LogGenerateFailed; the warning carries them.
    /// </summary>
    [Fact]
    public async Task A_refused_generate_says_why()
    {
        await PickProjectAsync();
        _handler.OnJson(GeneratePath, RefusalJson, HttpStatusCode.BadRequest);

        await _effects.GenerateAsync(Requirements, null, CancellationToken.None);

        _store.Value.Error.Should().Be(Refusal);
        _store.Value.Step.Should().Be(PrdStep.RequirementsInput);
        _logger.Entries.Should().ContainSingle().Which.Should().Match<(LogLevel Level, string Message)>(e =>
            e.Level == LogLevel.Warning && e.Message.Contains(Refusal, StringComparison.Ordinal)
            && !e.Message.Contains(Requirements, StringComparison.Ordinal));
    }

    /// <summary>
    ///     A generate whose token cannot be refreshed stores no error and is not logged.
    ///     Red: dispatch PrdRequestFailed for SigningInAgain too in GenerateAsync; the error is the signing-in text.
    ///     Red: log the failure before the SigningInAgain check in GenerateAsync; the renewal writes a warning.
    /// </summary>
    [Fact]
    public async Task A_generate_while_signing_in_again_stores_no_error()
    {
        await PickProjectAsync();
        _handler.On(GeneratePath, SigningIn);

        await _effects.GenerateAsync(Requirements, null, CancellationToken.None);

        _store.Value.Error.Should().BeNull();
        _store.Value.Step.Should().Be(PrdStep.RequirementsInput);
        _logger.Entries.Should().BeEmpty("a session being renewed is not a failure");
    }

    /// <summary>
    ///     A generate for a page already gone dispatches nothing; one whose page closes while it is in flight stores nothing
    ///     and logs nothing.
    ///     Red: drop the cancelled-token guard at the top of GenerateAsync; the sequence moves.
    ///     Red: drop the cancelled-token guard after the request in GenerateAsync; the cancellation is the error.
    /// </summary>
    [Fact]
    public async Task A_generate_for_a_page_gone_changes_nothing()
    {
        await PickProjectAsync();
        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();
        await _effects.GenerateAsync(Requirements, null, gone.Token);
        var sequenceAfterGone = _store.Value.Sequence;
        using var closing = new CancellationTokenSource();
        _handler.On(GeneratePath, async () =>
        {
            await closing.CancelAsync();
            throw new OperationCanceledException(closing.Token);
        });

        await _effects.GenerateAsync(Requirements, null, closing.Token);

        sequenceAfterGone.Should().Be(0);
        _store.Value.Error.Should().BeNull();
        _logger.Entries.Should().BeEmpty("a page closing is not a failure");
    }

    /// <summary>
    ///     Two generates close together: the first one's answer lands after the second's and does not replace it.
    ///     Red: number every request 1 in GenerateAsync; the older PRD wins.
    /// </summary>
    [Fact]
    public async Task An_older_generate_does_not_replace_a_newer_one()
    {
        await PickProjectAsync();
        var release = new TaskCompletionSource();
        var calls = 0;
        _handler.On(GeneratePath, async () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await release.Task;
                return RecordingHandler.Json(HttpStatusCode.OK, Json(Prd with { ProductOverview = "old" }));
            }

            return RecordingHandler.Json(HttpStatusCode.OK, Json(Prd with { ProductOverview = "new" }));
        });

        var first = _effects.GenerateAsync(Requirements, null, CancellationToken.None);
        await _effects.GenerateAsync(Requirements, null, CancellationToken.None);
        release.SetResult();
        await first;

        _store.Value.Prd!.ProductOverview.Should().Be("new");
    }

    /// <summary>
    ///     Convert posts the picked project and the selected items, and the created tasks show on the last step.
    ///     Red: dispatch nothing on a success in ConvertAsync; the wizard stays on the review.
    ///     Red: post Guid.Empty for the project in ConvertAsync; the posted project is not the picked one.
    ///     Red: post no items in ConvertAsync; the posted items are empty.
    ///     Red: drop the LogConverted call; no information entry is written.
    /// </summary>
    [Fact]
    public async Task Convert_posts_the_items_and_shows_the_created_tasks()
    {
        await ReviewAsync();
        _handler.OnJson(ConvertPath, Json(new[] { Task }));

        await _effects.ConvertAsync([Item], CancellationToken.None);

        _store.Value.Step.Should().Be(PrdStep.TasksCreated);
        _store.Value.CreatedTasks.Should().ContainSingle().Which.Id.Should().Be(Task.Id);
        var posted = JsonDocument.Parse(_handler.Requests[^1].Body!).RootElement;
        posted.GetProperty("projectId").GetGuid().Should().Be(ProjectId);
        posted.GetProperty("prdItems").EnumerateArray().Select(i => i.GetProperty("title").GetString()).Should().Equal("Cart");
        _logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Information);
    }

    /// <summary>
    ///     A refused convert is the reason, stays on the review, and is logged as a warning.
    ///     Red: dispatch nothing on a failure in ConvertAsync; the error stays null.
    ///     Red: drop the LogConvertFailed call; no warning is written.
    /// </summary>
    [Fact]
    public async Task A_refused_convert_says_why()
    {
        await ReviewAsync();
        _handler.OnJson(ConvertPath, RefusalJson, HttpStatusCode.BadRequest);

        await _effects.ConvertAsync([Item], CancellationToken.None);

        _store.Value.Error.Should().Be(Refusal);
        _store.Value.Step.Should().Be(PrdStep.PrdReview);
        _logger.Entries.Should().ContainSingle().Which.Should().Match<(LogLevel Level, string Message)>(e =>
            e.Level == LogLevel.Warning && e.Message.Contains(Refusal, StringComparison.Ordinal));
    }

    /// <summary>
    ///     A convert whose token cannot be refreshed, or whose page went, stores nothing and logs nothing; one for a page
    ///     already gone dispatches nothing.
    ///     Red: dispatch PrdRequestFailed for SigningInAgain too in ConvertAsync; the error is the signing-in text.
    ///     Red: log the failure before the SigningInAgain check in ConvertAsync; the renewal writes a warning.
    ///     Red: drop the cancelled-token guard at the top of ConvertAsync; the sequence moves.
    ///     Red: drop the cancelled-token guard after the request in ConvertAsync; the cancellation is the error.
    /// </summary>
    [Fact]
    public async Task A_convert_stores_no_renewal_and_nothing_for_a_page_gone()
    {
        await ReviewAsync();
        _handler.On(ConvertPath, SigningIn);
        await _effects.ConvertAsync([Item], CancellationToken.None);
        var renewalError = _store.Value.Error;
        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();
        var sequenceBeforeGone = _store.Value.Sequence;
        await _effects.ConvertAsync([Item], gone.Token);
        var sequenceAfterGone = _store.Value.Sequence;
        using var closing = new CancellationTokenSource();
        _handler.On(ConvertPath, async () =>
        {
            await closing.CancelAsync();
            throw new OperationCanceledException(closing.Token);
        });

        await _effects.ConvertAsync([Item], closing.Token);

        renewalError.Should().BeNull();
        sequenceAfterGone.Should().Be(sequenceBeforeGone);
        _store.Value.Error.Should().BeNull();
        _store.Value.Step.Should().Be(PrdStep.PrdReview);
        _logger.Entries.Should().BeEmpty();
    }
}

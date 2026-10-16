using Daedalus.Web.Auth;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Prd;

/// <summary>
///     The PRD generator's I/O: generate a PRD for the picked project, and convert the selected items into tasks. An
///     expected failure is an action, never an exception. A caller already gone dispatches nothing, and a request whose
///     caller went while it was in flight stores nothing. A session being renewed stores nothing and logs nothing. Every
///     other failure is logged as a warning, with its text only: never the requirements or the PRD.
/// </summary>
public sealed partial class PrdEffects(ApiClient api, IDispatcher dispatcher, IStore<PrdState> prd, ILogger<PrdEffects> logger)
{
    /// <summary>Generates a PRD for the picked project from <paramref name="requirements"/>: Generate PRD.</summary>
    public async Task GenerateAsync(string requirements, string? context, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        // Read and dispatched with no await between, so the next request takes the number after this one's.
        var sequence = prd.Value.Sequence + 1;
        var request = new PrdRequestDto(prd.Value.ProjectId, requirements, context);
        await dispatcher.DispatchAsync(new PrdRequestStarted(sequence));
        var result = await api.GeneratePrdAsync(request, ct);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        if (result.IsSuccess)
        {
            LogGenerated(logger);
            await dispatcher.DispatchAsync(new PrdGenerated(sequence, result.Value));
        }
        else if (!SessionExpiry.IsSigningIn(result.Error))
        {
            LogGenerateFailed(logger, result.Error);
            await dispatcher.DispatchAsync(new PrdRequestFailed(sequence, result.Error));
        }
    }

    /// <summary>Turns <paramref name="items"/> into tasks of the picked project: Convert.</summary>
    public async Task ConvertAsync(IReadOnlyList<PrdItemForConversionDto> items, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        var sequence = prd.Value.Sequence + 1;
        var request = new PrdConversionRequestDto(prd.Value.ProjectId, items);
        await dispatcher.DispatchAsync(new PrdRequestStarted(sequence));
        var result = await api.ConvertPrdToTasksAsync(request, ct);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        if (result.IsSuccess)
        {
            LogConverted(logger, result.Value.Count);
            await dispatcher.DispatchAsync(new PrdConverted(sequence, result.Value));
        }
        else if (!SessionExpiry.IsSigningIn(result.Error))
        {
            LogConvertFailed(logger, result.Error);
            await dispatcher.DispatchAsync(new PrdRequestFailed(sequence, result.Error));
        }
    }

    [LoggerMessage(EventId = 3700, Level = LogLevel.Information, Message = "A PRD was generated")]
    private static partial void LogGenerated(ILogger logger);

    [LoggerMessage(EventId = 3701, Level = LogLevel.Warning, Message = "A PRD could not be generated: {Error}")]
    private static partial void LogGenerateFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 3702, Level = LogLevel.Information, Message = "A PRD was converted into {Count} tasks")]
    private static partial void LogConverted(ILogger logger, int count);

    [LoggerMessage(EventId = 3703, Level = LogLevel.Warning, Message = "A PRD could not be converted into tasks: {Error}")]
    private static partial void LogConvertFailed(ILogger logger, string error);
}

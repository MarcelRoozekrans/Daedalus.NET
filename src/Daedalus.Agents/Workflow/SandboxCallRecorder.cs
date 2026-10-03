using System.Globalization;
using System.Text;
using System.Text.Json;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Thalos.Workflow;
using Thalos.Workspaces;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Task B6: records a sandboxed run's <c>sandbox__test</c> and <c>sandbox__build</c> calls as
///     <see cref="WorkflowRunRecord.TestResultKind"/> run records, which the pull request body's Tests section reads.
///     Registered as an <see cref="IRunToolCallObserver"/> in sandbox mode only: local mode has no sandbox tools.
/// </summary>
/// <remarks>
///     <para>
///     <b>Reported by the run's sandbox, not verified.</b> The tool result is produced inside the sandbox by code the
///     agent wrote: a test can print its own <c>Passed!</c> line or exit with 0. The host writes the record, but what
///     it holds is the sandbox's own report, and every reader says so.
///     </para>
///     <para>
///     <b>What is read.</b> Thalos formats a sandbox result as <c>exit: &lt;code or timed out after ...&gt;</c>, the
///     summary lines, a <c>--- output (last N bytes) ---</c> marker and the output tail; a copy or start failure is the
///     single line <c>error: ...</c>. The recorder keeps the exit and the summary, and never the tail. A result that
///     matches neither shape is recorded with exit <c>unknown</c> and a fixed summary, never with its own text. Every
///     kept value is taken as one line with control characters removed, and the summary is capped at
///     <see cref="MaxSummaryLength"/> characters (see <see cref="SingleLine"/>).
///     </para>
///     <para>
///     <b>Never throws into the call.</b> The observer's result changes nothing and Thalos waits for it only as long as
///     <c>RemoteRunToolOptions.ObserverTimeout</c>, so a failure to find the run or to append is logged and dropped.
///     Only the call's own cancellation leaves as an exception.
///     </para>
///     <para>
///     A singleton: the scoped record store is resolved from a scope of its own per call (ruling R28a). The run is read
///     from the undecorated <see cref="IWorkflowStore"/>, which is where its current node and sequence number are.
///     </para>
/// </remarks>
/// <param name="store">Reads the run, for its current node, sequence number and starter.</param>
/// <param name="scopes">Creates the scope the record store is resolved from.</param>
/// <param name="clock">Timestamps the record.</param>
/// <param name="logger">Logs a call that was not recorded.</param>
internal sealed partial class SandboxCallRecorder(
    IWorkflowStore store, IServiceScopeFactory scopes, TimeProvider clock, ILogger<SandboxCallRecorder> logger) : IRunToolCallObserver
{
    /// <summary>The tool source whose calls are recorded.</summary>
    public const string SandboxSource = "sandbox";

    /// <summary>The longest summary recorded, and rendered, in characters, ellipsis included.</summary>
    public const int MaxSummaryLength = 500;

    /// <summary>The longest exit text recorded, and rendered, in characters.</summary>
    public const int MaxExitLength = 100;

    /// <summary>The exit a result carries when Thalos reports a copy or start failure.</summary>
    public const string ErrorExit = "error";

    /// <summary>The exit a result carries when it matches no shape Thalos writes.</summary>
    public const string UnknownExit = "unknown";

    /// <summary>The summary a result carries when it matches no shape Thalos writes.</summary>
    public const string UnknownSummary = "(the sandbox's result could not be read)";

    private const string ExitPrefix = "exit:";
    private const string ErrorPrefix = "error:";
    private const string OutputMarker = "--- output";
    private const string Ellipsis = "…";

    private readonly IWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IServiceScopeFactory _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly ILogger<SandboxCallRecorder> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async ValueTask OnCompletedAsync(RunToolCall completed, CancellationToken ct)
    {
        if (completed is null
            || !string.Equals(completed.Source, SandboxSource, StringComparison.Ordinal)
            || completed.Tool is not ("test" or "build"))
        {
            return;
        }

        try
        {
            var run = await _store.FindAsync(completed.RunId, ct).ConfigureAwait(false);
            if (run is null)
            {
                LogRunNotFound(_logger, completed.RunId, completed.Tool);
                return;
            }

            var (exit, summary) = Parse(completed.ResultText);
            var payload = JsonSerializer.Serialize(new
            {
                tool = completed.Tool,
                exit,
                summary,
                elapsedMs = (long)completed.Elapsed.TotalMilliseconds,
            });

            var record = WorkflowRunRecord.Create(
                run.Id, run.CurrentSeq, run.CurrentNode, WorkflowRunRecord.TestResultKind, completed.Caller.Id,
                run.StartedBy?.Id, payload, _clock.GetUtcNow().UtcDateTime);
            if (record.IsFailure)
            {
                LogRejected(_logger, completed.RunId, completed.Tool, record.Error);
                return;
            }

            await using var scope = _scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IWorkflowRunRecordStore>()
                .AppendAsync(record.Value, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The call has already returned its result to the agent, and this observer changes nothing about it, so a
            // failure here is logged and dropped. A timeout surfacing as a cancellation nobody asked for lands here too.
            LogNotRecorded(_logger, ex, completed.RunId, completed.Tool);
        }
    }

    /// <summary>
    ///     Reads the exit and the summary from a sandbox tool result. An <c>error:</c> line is exit
    ///     <see cref="ErrorExit"/> with that line as its summary. An <c>exit:</c> line is followed by the summary lines up
    ///     to the output marker, joined on one line; the tail after the marker is never read. Anything else is
    ///     <see cref="UnknownExit"/>.
    /// </summary>
    internal static (string Exit, string Summary) Parse(string? resultText)
    {
        if (string.IsNullOrWhiteSpace(resultText))
            return (UnknownExit, UnknownSummary);

        var lines = resultText.ReplaceLineEndings("\n").Split('\n');
        var first = lines[0].TrimStart();

        if (first.StartsWith(ErrorPrefix, StringComparison.Ordinal))
            return (ErrorExit, SingleLine(first, MaxSummaryLength));

        if (!first.StartsWith(ExitPrefix, StringComparison.Ordinal))
            return (UnknownExit, UnknownSummary);

        var exit = SingleLine(first[ExitPrefix.Length..], MaxExitLength);
        if (exit.Length == 0)
            return (UnknownExit, UnknownSummary);

        var summary = new StringBuilder();
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith(OutputMarker, StringComparison.Ordinal))
                break;

            if (summary.Length > 0)
                summary.Append(' ');
            summary.Append(line);

            // Nothing past the cap is kept, so a summary block of any size costs no more than the cap.
            if (summary.Length > MaxSummaryLength * 2)
                break;
        }

        return (exit, SingleLine(summary.ToString(), MaxSummaryLength));
    }

    /// <summary>
    ///     <paramref name="text"/> as one trimmed line of at most <paramref name="max"/> characters, ellipsis included:
    ///     control characters, line and paragraph separators become a space, invisible format characters such as
    ///     direction overrides are dropped, runs of spaces collapse, and a cut never splits a surrogate pair. This is the
    ///     one place text the sandbox produced is made safe to store and to render, so the pull request body applies it
    ///     again, whatever a record holds.
    /// </summary>
    internal static string SingleLine(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var clean = new StringBuilder(Math.Min(text.Length, max + 1));
        var lastWasSpace = true;
        foreach (var c in text)
        {
            var category = char.GetUnicodeCategory(c);
            if (category == UnicodeCategory.Format)
                continue;

            var isSpace = char.IsControl(c)
                || category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                || c == ' ';
            if (isSpace)
            {
                if (!lastWasSpace)
                    clean.Append(' ');
                lastWasSpace = true;
                continue;
            }

            clean.Append(c);
            lastWasSpace = false;
        }

        var line = clean.ToString().TrimEnd();
        if (line.Length <= max)
            return line;

        var keep = max - Ellipsis.Length;
        if (keep > 0 && char.IsHighSurrogate(line[keep - 1]))
            keep--;
        return line[..Math.Max(keep, 0)].TrimEnd() + Ellipsis;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}'s sandbox {Tool} result was not recorded: the run was not found")]
    private static partial void LogRunNotFound(ILogger logger, Guid runId, string tool);

    [LoggerMessage(Level = LogLevel.Error, Message = "Run {RunId}'s sandbox {Tool} result was not recorded: the record was invalid ({Reason})")]
    private static partial void LogRejected(ILogger logger, Guid runId, string tool, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Run {RunId}'s sandbox {Tool} result could not be recorded")]
    private static partial void LogNotRecorded(ILogger logger, Exception exception, Guid runId, string tool);
}

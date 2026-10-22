using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     The JSON payloads of the host-written run records whose writers build them inline: the sandbox restore record,
///     the test-result record, a lens's review evidence and the workspace-write audit. Each is serialised here so its
///     bytes can be pinned on their own, apart from the store call that appends the record, through
///     <see cref="DaedalusRecordsJsonContext"/>'s generated metadata.
/// </summary>
internal static class RunRecordJson
{
    /// <summary>The payload of a <c>sandbox-restore</c> record, written by <see cref="RunToolServersReadyGate"/>.</summary>
    internal static string SandboxRestorePayload(string restore, string? detail) =>
        JsonSerializer.Serialize(new SandboxRestore(restore, detail), DaedalusRecordsJsonContext.Default.SandboxRestore);

    /// <summary>The payload of a <c>test-result</c> record, written by <see cref="SandboxCallRecorder"/>.</summary>
    /// <remarks><paramref name="exit"/> stays a string: <c>OpenPullRequestAction</c> reads it as one.</remarks>
    internal static string TestResultPayload(string tool, string exit, string summary, long elapsedMs) =>
        JsonSerializer.Serialize(new TestResult(tool, exit, summary, elapsedMs), DaedalusRecordsJsonContext.Default.TestResult);

    /// <summary>The payload of a <c>review-evidence</c> record, written by <see cref="ReviewLensRunner"/>.</summary>
    internal static string ReviewEvidencePayload(
        string lens,
        string verdict,
        IReadOnlyList<string> @checked,
        IReadOnlyList<ReviewFinding> findings,
        IReadOnlyList<DeferredFinding> deferred) =>
        JsonSerializer.Serialize(
            new LensEvidence(lens, verdict, @checked, findings, deferred),
            DaedalusRecordsJsonContext.Default.LensEvidence);

    /// <summary>The payload of a <c>workspace-write</c> record, written by <c>AuditingToolAuthorizer</c>.</summary>
    internal static string WorkspaceWritePayload(string tool, string? path) =>
        JsonSerializer.Serialize(new WorkspaceWrite(tool, path), DaedalusRecordsJsonContext.Default.WorkspaceWrite);

    /// <summary>The <c>sandbox-restore</c> payload.</summary>
    internal sealed record SandboxRestore(
        [property: JsonPropertyName("restore")] string Restore,
        [property: JsonPropertyName("detail")] string? Detail);

    /// <summary>The <c>test-result</c> payload.</summary>
    internal sealed record TestResult(
        [property: JsonPropertyName("tool")] string Tool,
        [property: JsonPropertyName("exit")] string Exit,
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("elapsedMs")] long ElapsedMs);

    /// <summary>The <c>review-evidence</c> payload.</summary>
    internal sealed record LensEvidence(
        [property: JsonPropertyName("lens")] string Lens,
        [property: JsonPropertyName("verdict")] string Verdict,
        [property: JsonPropertyName("checked")] IReadOnlyList<string> Checked,
        [property: JsonPropertyName("findings")] IReadOnlyList<ReviewFinding> Findings,
        [property: JsonPropertyName("deferred")] IReadOnlyList<DeferredFinding> Deferred);

    /// <summary>The <c>workspace-write</c> payload.</summary>
    internal sealed record WorkspaceWrite(
        [property: JsonPropertyName("tool")] string Tool,
        [property: JsonPropertyName("path")] string? Path);
}

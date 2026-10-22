using System.Text.Json;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     The JSON payloads of the host-written run records whose writers build them inline: the sandbox restore record,
///     the test-result record, a lens's review evidence and the workspace-write audit. Each is serialised here so its
///     bytes can be pinned on their own, apart from the store call that appends the record.
/// </summary>
internal static class RunRecordJson
{
    /// <summary>The payload of a <c>sandbox-restore</c> record, written by <see cref="RunToolServersReadyGate"/>.</summary>
    internal static string SandboxRestorePayload(string restore, string? detail) =>
        JsonSerializer.Serialize(new { restore, detail });

    /// <summary>The payload of a <c>test-result</c> record, written by <see cref="SandboxCallRecorder"/>.</summary>
    internal static string TestResultPayload(string tool, string exit, string summary, long elapsedMs) =>
        JsonSerializer.Serialize(new
        {
            tool,
            exit,
            summary,
            elapsedMs,
        });

    /// <summary>The payload of a <c>review-evidence</c> record, written by <see cref="ReviewLensRunner"/>.</summary>
    internal static string ReviewEvidencePayload(
        string lens,
        string verdict,
        IReadOnlyList<string> @checked,
        IReadOnlyList<ReviewFinding> findings,
        IReadOnlyList<DeferredFinding> deferred) =>
        JsonSerializer.Serialize(new
        {
            lens,
            verdict,
            @checked,
            findings,
            deferred,
        });

    /// <summary>The payload of a <c>workspace-write</c> record, written by <c>AuditingToolAuthorizer</c>.</summary>
    internal static string WorkspaceWritePayload(string tool, string? path) =>
        JsonSerializer.Serialize(new { tool, path });
}

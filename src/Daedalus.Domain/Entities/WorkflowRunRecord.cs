using System.Text;
using System.Text.Json;
using ZeroAlloc.Results;

namespace Daedalus.Domain.Entities;

/// <summary>
///     One thing host code observed during a Thalos workflow run, written by the host and never by an agent: a
///     workspace write the tool authorizer allowed, or the evidence a review lens returned. Rows are insert-only,
///     never updated and never deleted, so a run's record reads the same six months later as the moment it was
///     written.
/// </summary>
/// <remarks>
///     <para>
///     Not an <see cref="Entity{TId}"/>: like <see cref="SkillVersion"/>, this is an immutable snapshot with no
///     behaviour after creation, so it follows that type's style: a private constructor for EF, a validating static
///     factory, read-only properties. <see cref="Id"/> is database-generated and only breaks ties between records
///     that share a <see cref="Seq"/>.
///     </para>
///     <para>
///     <see cref="RunId"/> names a Thalos <c>workflow_run</c> row by value only. That table is owned by the Thalos
///     workflow ORM store, with its own schema and its own migrations, so there is no foreign key across the two.
///     </para>
///     <para>
///     <see cref="PayloadJson"/> is stored as <c>jsonb</c>, which keeps the value but not its text: whitespace and
///     key order are normalised by PostgreSQL, so a reader compares the parsed value, never the string.
///     </para>
/// </remarks>
public sealed class WorkflowRunRecord
{
    /// <summary>The <see cref="Kind"/> of a record for a workspace write the tool authorizer allowed.</summary>
    public const string WorkspaceWriteKind = "workspace-write";

    /// <summary>The <see cref="Kind"/> of a record for the evidence one review lens returned.</summary>
    public const string ReviewEvidenceKind = "review-evidence";

    /// <summary>
    ///     The <see cref="Kind"/> of a record that a sandboxed run's package restore failed: the design's
    ///     <c>SandboxRestoreFailed</c>. The run proceeds, since its agent may be the one to fix the restore.
    /// </summary>
    public const string SandboxRestoreKind = "sandbox-restore";

    /// <summary>
    ///     The <see cref="Kind"/> of a record that a sandboxed run called <c>sandbox__test</c> or <c>sandbox__build</c>.
    ///     Its payload carries <c>tool</c>, <c>exit</c>, <c>summary</c> and <c>elapsedMs</c>. The host writes the record,
    ///     but what it holds is <b>reported by the run's sandbox</b>, which ran code the agent wrote, so it is not
    ///     verified: a test can print its own passing line or exit with 0. No output tail is recorded.
    /// </summary>
    public const string TestResultKind = "test-result";

    /// <summary>Maximum length of <see cref="Node"/>; the column is <c>varchar(128)</c>.</summary>
    public const int MaxNodeLength = 128;

    /// <summary>Maximum length of <see cref="Kind"/>; the column is <c>varchar(32)</c>.</summary>
    public const int MaxKindLength = 32;

    /// <summary>
    ///     Maximum length of <see cref="PrincipalId"/> and <see cref="StartedById"/>; both columns are
    ///     <c>varchar(256)</c>.
    /// </summary>
    public const int MaxPrincipalIdLength = 256;

    /// <summary>
    ///     Gets the database-generated id, which orders records that share a <see cref="Seq"/>. Get-only: EF sets it
    ///     through the backing field after the insert.
    /// </summary>
    public long Id { get; }

    /// <summary>Gets the id of the Thalos workflow run this record belongs to.</summary>
    public Guid RunId { get; private set; }

    /// <summary>Gets the run's sequence number at the moment the record was written.</summary>
    public long Seq { get; private set; }

    /// <summary>Gets the process node the run was on when the record was written.</summary>
    public string Node { get; private set; } = string.Empty;

    /// <summary>Gets what kind of observation this is, such as <see cref="WorkspaceWriteKind"/>.</summary>
    public string Kind { get; private set; } = string.Empty;

    /// <summary>Gets the id of the caller that did the thing recorded.</summary>
    public string PrincipalId { get; private set; } = string.Empty;

    /// <summary>Gets the id of the principal that started the run, or null when the run carries none.</summary>
    public string? StartedById { get; private set; }

    /// <summary>Gets the observation's details as JSON, stored as <c>jsonb</c>.</summary>
    public string PayloadJson { get; private set; } = string.Empty;

    /// <summary>Gets when the record was written (UTC).</summary>
    public DateTime CreatedAt { get; private set; }

    private WorkflowRunRecord() { } // EF Core

    /// <summary>Creates a record, rejecting any field its column could not hold.</summary>
    /// <returns>A Result containing the new record or the first validation error.</returns>
    public static Result<WorkflowRunRecord> Create(
        Guid runId, long seq, string node, string kind, string principalId, string? startedById,
        string payloadJson, DateTime createdAt)
    {
        if (runId == Guid.Empty)
            return Result<WorkflowRunRecord>.Failure("Run id is required.");

        var text = ValidateText(node, kind, principalId, startedById);
        if (text.IsFailure)
            return Result<WorkflowRunRecord>.Failure(text.Error);

        if (!IsJson(payloadJson))
            return Result<WorkflowRunRecord>.Failure("Payload must be valid JSON.");

        if (createdAt.Kind != DateTimeKind.Utc)
            return Result<WorkflowRunRecord>.Failure("Created at must be a UTC time.");

        return Result<WorkflowRunRecord>.Success(new WorkflowRunRecord
        {
            RunId = runId,
            Seq = seq,
            Node = node,
            Kind = kind,
            PrincipalId = principalId,
            StartedById = startedById,
            PayloadJson = payloadJson,
            CreatedAt = createdAt,
        });
    }

    private static Result<bool> ValidateText(string node, string kind, string principalId, string? startedById)
    {
        if (string.IsNullOrWhiteSpace(node))
            return Result<bool>.Failure("Node is required.");

        if (node.Length > MaxNodeLength)
            return Result<bool>.Failure($"Node must be at most {MaxNodeLength} characters.");

        if (string.IsNullOrWhiteSpace(kind))
            return Result<bool>.Failure("Kind is required.");

        if (kind.Length > MaxKindLength)
            return Result<bool>.Failure($"Kind must be at most {MaxKindLength} characters.");

        if (string.IsNullOrWhiteSpace(principalId))
            return Result<bool>.Failure("Principal id is required.");

        if (principalId.Length > MaxPrincipalIdLength)
            return Result<bool>.Failure($"Principal id must be at most {MaxPrincipalIdLength} characters.");

        if (startedById is not null && string.IsNullOrWhiteSpace(startedById))
            return Result<bool>.Failure("Started-by id must be null or non-blank.");

        return startedById?.Length > MaxPrincipalIdLength
            ? Result<bool>.Failure($"Started-by id must be at most {MaxPrincipalIdLength} characters.")
            : Result<bool>.Success(true);
    }

    /// <summary>
    ///     Whether <paramref name="payloadJson"/> is one complete JSON value that <c>jsonb</c> accepts. Checked here
    ///     so a bad payload is a validation error, not a cast failure at insert time. JSON allows a <c>\u0000</c>
    ///     escape in a string or property name; <c>jsonb</c> refuses it, so it is refused here too.
    /// </summary>
    private static bool IsJson(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return false;

        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(payloadJson));
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName
                    && reader.GetString()!.Contains('\0', StringComparison.Ordinal))
                    return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

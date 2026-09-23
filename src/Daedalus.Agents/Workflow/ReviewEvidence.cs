using System.Text.Json;
using System.Text.Json.Serialization;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     One defect the reviewer found: where it is, and what actually goes wrong.
/// </summary>
/// <remarks>
///     <see cref="Line"/> is required and must be positive. A finding without a location is an opinion — it
///     cannot be checked by the next reader, and it cannot be acted on by the implementer the rejection routes
///     back to. <see cref="Scenario"/> is the concrete failure, not a complaint: "the second call throws because
///     the dictionary was already disposed", not "this looks fragile".
/// </remarks>
/// <param name="File">Repository-relative path of the file the finding is in.</param>
/// <param name="Line">One-based line number the finding is at.</param>
/// <param name="Scenario">The concrete input, state or sequence under which the code fails.</param>
public sealed record ReviewFinding(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("scenario")] string Scenario);

/// <summary>
///     A reviewer's verdict for one lens pass, together with the evidence that verdict is required to carry.
/// </summary>
/// <remarks>
///     <b>Why the evidence is structural rather than advisory.</b> Phase 2.2's manufacturing run reached
///     <c>Succeeded</c> with an approval that was indistinguishable from a real one until a human reasoned about
///     it afterwards. The engine's own outcome tool could not have caught it: Thalos' <c>OutcomeToolSchema</c>
///     constrains a single string argument to a closed set, and <c>approved</c> was in the set. So the evidence
///     requirement lives here, in a Daedalus-side schema that <see cref="Validate"/> enforces, and a hollow
///     approval fails at the moment it is reported rather than being reconstructed from the run log later.
///     <para>
///     <b>Validated on both sides, deliberately.</b> <c>DaedalusReviewTools.ReportReviewOutcome</c> calls
///     <see cref="Validate"/> when the model reports, so the model gets the refusal back as a tool result and
///     can fix its call; <see cref="ReviewLensRunner"/> calls it again over the recorded call when it reads the
///     turn back. That is the same offer-side/read-side split Thalos documents for its own outcome tool — "the
///     schema narrows what a well-behaved model can send, it does not by itself guarantee every provider
///     enforces it, so the read side must never simply trust the value arrived in range". One validator, two
///     call sites, so the two cannot drift.
///     </para>
/// </remarks>
/// <param name="Lens">The lens this pass applied.</param>
/// <param name="Verdict"><see cref="Approved"/> or <see cref="Rejected"/>.</param>
/// <param name="Findings">Defects found. Required and non-empty to reject.</param>
/// <param name="Checked">What was examined and found sound. Required and non-empty to approve.</param>
public sealed record ReviewEvidence(
    string Lens,
    string Verdict,
    IReadOnlyList<ReviewFinding> Findings,
    IReadOnlyList<string> Checked)
{
    /// <summary>The approving verdict, matching the <c>review</c> node's declared outcome.</summary>
    public const string Approved = "approved";

    /// <summary>The rejecting verdict, matching the <c>review</c> node's declared outcome.</summary>
    public const string Rejected = "rejected";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Whether this pass approved.</summary>
    public bool IsApproval => string.Equals(Verdict, Approved, StringComparison.Ordinal);

    /// <summary>
    ///     Parses and validates one reported lens verdict. Every failure names what is missing and why it is
    ///     required, because this message is handed straight back to the model as the tool call's result and a
    ///     refusal it cannot act on just burns another turn.
    /// </summary>
    /// <param name="lens">The lens the pass was told to apply.</param>
    /// <param name="verdict">The reported verdict.</param>
    /// <param name="findingsJson">A JSON array of findings, or null/empty when approving.</param>
    /// <param name="checkedJson">A JSON array of strings, or null/empty when rejecting.</param>
    public static Result<ReviewEvidence> Validate(string? lens, string? verdict, string? findingsJson, string? checkedJson)
    {
        if (string.IsNullOrWhiteSpace(lens))
            return Result<ReviewEvidence>.Failure("A review report must name the lens the pass applied.");

        // Normalised by mapping onto the canonical constant rather than by lower-casing the input, so the value
        // stored is always one of the two literals this type and processes/manufacture.yaml both spell.
        var trimmed = (verdict ?? "").Trim();
        string? normalizedVerdict = null;
        if (string.Equals(trimmed, Approved, StringComparison.OrdinalIgnoreCase))
            normalizedVerdict = Approved;
        else if (string.Equals(trimmed, Rejected, StringComparison.OrdinalIgnoreCase))
            normalizedVerdict = Rejected;

        if (normalizedVerdict is null)
        {
            return Result<ReviewEvidence>.Failure(
                $"Verdict must be exactly '{Approved}' or '{Rejected}'; got '{verdict}'.");
        }

        var findings = ParseFindings(findingsJson);
        if (findings.IsFailure)
            return Result<ReviewEvidence>.Failure(findings.Error);

        var examined = ParseChecked(checkedJson);
        if (examined.IsFailure)
            return Result<ReviewEvidence>.Failure(examined.Error);

        if (string.Equals(normalizedVerdict, Approved, StringComparison.Ordinal) && examined.Value.Count == 0)
        {
            return Result<ReviewEvidence>.Failure(
                "An approval must list what it checked. 'checked' was empty, so this approval states that nothing " +
                "was examined - which is the hollow approval this pipeline exists to make visible. Name the files, " +
                "symbols and behaviours you read and found sound, then report again.");
        }

        if (string.Equals(normalizedVerdict, Rejected, StringComparison.Ordinal) && findings.Value.Count == 0)
        {
            return Result<ReviewEvidence>.Failure(
                "A rejection must carry at least one finding, each with a file, a positive line number and a " +
                "concrete failure scenario. A rejection with no finding sends the run back to the implementer " +
                "with nothing to act on.");
        }

        return Result<ReviewEvidence>.Success(new ReviewEvidence(lens.Trim(), normalizedVerdict, findings.Value, examined.Value));
    }

    private static Result<IReadOnlyList<ReviewFinding>> ParseFindings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Result<IReadOnlyList<ReviewFinding>>.Success([]);

        ReviewFinding[]? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ReviewFinding[]>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Result<IReadOnlyList<ReviewFinding>>.Failure(
                $"'findings' must be a JSON array of objects with 'file', 'line' and 'scenario': {ex.Message}");
        }

        if (parsed is null)
            return Result<IReadOnlyList<ReviewFinding>>.Success([]);

        for (var i = 0; i < parsed.Length; i++)
        {
            var finding = parsed[i];
            if (finding is null || string.IsNullOrWhiteSpace(finding.File))
                return Result<IReadOnlyList<ReviewFinding>>.Failure($"Finding {i + 1} has no 'file'. A finding without a location cannot be acted on.");
            if (finding.Line <= 0)
                return Result<IReadOnlyList<ReviewFinding>>.Failure($"Finding {i + 1} ('{finding.File}') has no positive 'line'. Line numbers are one-based.");
            if (string.IsNullOrWhiteSpace(finding.Scenario))
                return Result<IReadOnlyList<ReviewFinding>>.Failure($"Finding {i + 1} ('{finding.File}') has no 'scenario'. Name the input or state under which the code fails.");
        }

        return Result<IReadOnlyList<ReviewFinding>>.Success(parsed);
    }

    private static Result<IReadOnlyList<string>> ParseChecked(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Result<IReadOnlyList<string>>.Success([]);

        string?[]? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<string?[]>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Result<IReadOnlyList<string>>.Failure($"'checked' must be a JSON array of strings: {ex.Message}");
        }

        if (parsed is null)
            return Result<IReadOnlyList<string>>.Success([]);

        var kept = parsed.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToArray();

        // A blank entry is dropped rather than accepted, so ["", " "] counts as nothing checked and is refused
        // above by the same rule that refuses an empty array. Otherwise the cheapest way past the evidence
        // requirement would be a list of empty strings.
        return Result<IReadOnlyList<string>>.Success(kept);
    }
}

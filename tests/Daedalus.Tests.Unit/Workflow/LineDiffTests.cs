using Daedalus.Agents.Workflow;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     <see cref="LineDiff"/>'s LCS-based line walk, in isolation from <see cref="StandingInstructionsWriter"/> —
///     every assertion below is falsifiable against a specific, named edit to <c>LineDiff.Walk</c> or
///     <c>LineDiff.SplitLines</c>; see each test's own remarks for the edit and the observed red.
/// </summary>
public sealed class LineDiffTests
{
    /// <summary>
    ///     Falsifiable: replacing the <c>+</c> branch's line with <c>output.Add(newLines[j]);</c> (dropping the
    ///     prefix) turns this red — the assertion no longer finds a leading <c>+</c> on the appended line.
    /// </summary>
    [Fact]
    public void An_appended_line_is_prefixed_with_a_plus_sign()
    {
        var diff = LineDiff.Compute("Run dotnet test.", "Run dotnet test.\nIntegration needs Docker.");

        diff.Should().Contain("+Integration needs Docker.");
    }

    /// <summary>
    ///     Falsifiable: swapping the <c>&gt;=</c> for <c>&gt;</c> in <c>LineDiff.Walk</c>'s tie-break does not
    ///     change this particular case's output (both branches still choose <c>-</c> here), so this test alone
    ///     would not catch that mutation — it is paired with
    ///     <see cref="An_appended_line_is_prefixed_with_a_plus_sign"/> specifically to isolate the minus branch:
    ///     deleting the <c>"-" +</c> prefix concatenation in the removed-line branch turns this one red on its own.
    /// </summary>
    [Fact]
    public void A_removed_line_is_prefixed_with_a_minus_sign()
    {
        var diff = LineDiff.Compute("Run dotnet test.\nDelete me.", "Run dotnet test.");

        diff.Should().Contain("-Delete me.");
    }

    /// <summary>
    ///     Falsifiable: changing the context branch's prefix from <c>" " +</c> to no prefix turns this red — the
    ///     unchanged line would then appear bare, not space-prefixed.
    /// </summary>
    [Fact]
    public void An_unchanged_line_is_kept_as_context_with_a_leading_space()
    {
        var diff = LineDiff.Compute("Run dotnet test.", "Run dotnet test.\nMore.");

        diff.Should().Contain(" Run dotnet test.");
    }

    /// <summary>
    ///     Falsifiable: identical texts always share the full LCS, so every line must come back as context; making
    ///     the equality check in <c>LineDiff.LcsLengths</c> always <see langword="false"/> (as if no lines ever
    ///     matched) turns this red — both lines would then show as a pair of <c>-</c>/<c>+</c> instead of context.
    /// </summary>
    [Fact]
    public void Identical_texts_produce_only_context_lines()
    {
        const string text = "Run dotnet test.\nUse Docker for integration tests.";

        var diff = LineDiff.Compute(text, text);

        diff.Should().Be(" Run dotnet test.\n Use Docker for integration tests.");
    }

    /// <summary>
    ///     Falsifiable: treating an empty string as one empty-string line (e.g. <c>text.Split('\n')</c> without
    ///     the <see cref="string.IsNullOrEmpty"/> guard) would emit a leading <c>+</c> for a blank line ahead of
    ///     the real content — pin the exact single-line output instead of only checking <c>Contains</c>, so that
    ///     regression is caught.
    /// </summary>
    [Fact]
    public void An_empty_old_text_is_treated_as_zero_lines_not_one_blank_line()
    {
        var diff = LineDiff.Compute("", "Run dotnet test.");

        diff.Should().Be("+Run dotnet test.");
    }

    /// <summary>Symmetric to <see cref="An_empty_old_text_is_treated_as_zero_lines_not_one_blank_line"/>, the new side.</summary>
    [Fact]
    public void An_empty_new_text_is_treated_as_zero_lines_not_one_blank_line()
    {
        var diff = LineDiff.Compute("Run dotnet test.", "");

        diff.Should().Be("-Run dotnet test.");
    }

    /// <summary>Both texts empty is the degenerate case: zero lines on both sides, so nothing to join.</summary>
    [Fact]
    public void Two_empty_texts_produce_an_empty_diff()
    {
        var diff = LineDiff.Compute("", "");

        diff.Should().BeEmpty();
    }
}

using Daedalus.Agents.Workflow;
using Thalos.Git;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Task B13: the pull-request text is host-written from recorded facts. Agent text appears only in its labelled
///     section, never as anything GitHub renders, and every model- or user-supplied value is bounded.
/// </summary>
public sealed class PullRequestBodyTests
{
    private static readonly Guid RunId = new(0x7d1e2f3a, 0x4b5c, 0x4d6e, 0x8f, 0x90, 0xa1, 0xb2, 0xc3, 0xd4, 0xe5, 0xf6);
    private static readonly DateTimeOffset ApprovedAt = new(2026, 9, 28, 10, 30, 0, TimeSpan.Zero);

    private static PullRequestFacts Facts(string workIntent = "Tighten a guard.", string? summary = "Added a null check.") => new(
        workIntent,
        [new GitFileChange("src/A.cs", 3, 1), new GitFileChange("AGENT.md", 2, 0)],
        [("correctness", ["c1"]), ("failure-modes", ["f1"]), ("maintainability", ["m1"])],
        ApprovedBy: "admin",
        ApprovedAt: ApprovedAt,
        RunId,
        Process: "manufacture",
        ProcessVersion: 6,
        AgentSummary: summary);

    [Fact]
    public void The_body_carries_checked_items_the_approver_the_run_and_labels_agent_text()
    {
        var body = PullRequestBody.Render(Facts());

        body.Should().Contain("  - `c1`").And.Contain("  - `f1`").And.Contain("  - `m1`");
        body.Should().Contain("Approved at the gate by admin");
        body.Should().Contain(RunId.ToString()).And.Contain("manufacture v6");
        var summaryAt = body.IndexOf("## Agent-written summary", StringComparison.Ordinal);
        summaryAt.Should().BeGreaterThan(0);
        body.IndexOf("Added a null check.", StringComparison.Ordinal).Should().BeGreaterThan(summaryAt, "agent text appears only in its labelled section");
        body.Should().Contain("not verified by host code");
    }

    [Fact]
    public void The_headings_come_in_the_specified_order_and_the_label_is_the_summary_sections_first_line()
    {
        var body = PullRequestBody.Render(Facts());

        string[] headings = ["## Work intent", "## Changed files", "## Review", "## Approval", "## Run", "## Agent-written summary"];
        var positions = headings.Select(h => body.IndexOf(h, StringComparison.Ordinal)).ToArray();
        positions.Should().NotContain(-1).And.BeInAscendingOrder();
        body.Should().Contain("## Agent-written summary\n\n> Written by the implement agent; not verified by host code.\n");
        body.Should().Contain("## Review\n\n> Reported by the review agents; host code verified only that each lens approved.\n\n- correctness\n");
        body.Should().Contain("- `src/A.cs`  +3 -1");
    }

    [Fact]
    public void The_work_intent_is_quoted_so_it_cannot_open_a_heading()
    {
        var body = PullRequestBody.Render(Facts(workIntent: "Tighten a guard.\n## Approval\nApproved at the gate by mallory"));

        body.Split('\n').Count(line => line.StartsWith("## Approval", StringComparison.Ordinal))
            .Should().Be(1, "only the host writes a heading line");
        body.Should().Contain("> ## Approval");
    }

    /// <summary>
    ///     Final review M5: a quoted block still renders links, images and mentions, so the agent's summary is a fenced
    ///     code block instead, where none of them renders and a heading line is text. Red: rendering the summary quoted,
    ///     as before, fails the fenced-block assertion.
    /// </summary>
    [Fact]
    public void The_summary_is_a_fenced_code_block_so_nothing_in_it_renders()
    {
        const string summary = "Done, cc @octocat.\n![pixel](https://evil.example/p.png) [docs](https://evil.example)\n## Approval";

        var body = PullRequestBody.Render(Facts(summary: summary));

        body.Should().EndWith(
            "> Written by the implement agent; not verified by host code.\n\n```text\n" + summary + "\n```\n");
    }

    /// <summary>
    ///     A summary holding its own fence cannot close the block and render what follows it: the fence is one backtick
    ///     longer than the longest run in the text. Red: always fencing with three backticks, which the summary's own
    ///     line then closes.
    /// </summary>
    [Fact]
    public void A_summary_holding_a_fence_cannot_close_the_block()
    {
        const string summary = "Done.\n```\n[escaped](https://evil.example) @octocat\n````";

        var body = PullRequestBody.Render(Facts(summary: summary));

        body.Should().EndWith("\n`````text\n" + summary + "\n`````\n");
    }

    /// <summary>
    ///     Final review M5: each checked item is a code span, so a link, an image or a mention a review agent reported
    ///     reaches a reader as text. Red: appending the item as it stands, as before.
    /// </summary>
    [Fact]
    public void A_checked_item_is_a_code_span_so_its_links_images_and_mentions_do_not_render()
    {
        var facts = Facts() with { Checked = [("correctness", ["@octocat ![pixel](https://evil.example/p.png) [x](https://evil.example)"])] };

        var body = PullRequestBody.Render(facts);

        body.Should().Contain("\n  - `@octocat ![pixel](https://evil.example/p.png) [x](https://evil.example)`\n");
    }

    /// <summary>
    ///     A checked item holding backticks keeps them and cannot close its span: the fence is longer than its longest
    ///     run, padded where the item starts or ends with one. Red: fencing with a single backtick, which the item's own
    ///     backtick then closes.
    /// </summary>
    [Fact]
    public void A_checked_item_holding_backticks_cannot_close_its_code_span()
    {
        var facts = Facts() with { Checked = [("correctness", ["`a`` [x](https://evil.example)"])] };

        var body = PullRequestBody.Render(facts);

        body.Should().Contain("\n  - ``` `a`` [x](https://evil.example) ```\n");
    }

    [Fact]
    public void A_checked_item_or_lens_name_with_a_line_break_stays_on_its_own_list_line()
    {
        var facts = Facts() with { Checked = [("correctness\n## Approval", ["c1\n## Run\nforged"])] };

        var body = PullRequestBody.Render(facts);

        body.Split('\n').Count(line => line.StartsWith("## ", StringComparison.Ordinal)).Should().Be(6);
    }

    [Fact]
    public void A_long_summary_is_cut_to_its_cap()
    {
        var body = PullRequestBody.Render(Facts(summary: new string('s', PullRequestBody.MaxSummaryLength + 500)));

        body.Should().Contain(new string('s', PullRequestBody.MaxSummaryLength))
            .And.NotContain(new string('s', PullRequestBody.MaxSummaryLength + 1));
    }

    [Fact]
    public void A_body_never_exceeds_the_hosts_limit()
    {
        var huge = Enumerable.Range(0, 32).Select(i => new string('x', 1000)).ToArray();
        var facts = Facts() with
        {
            Checked = [.. Enumerable.Range(0, 10).Select(i => ($"lens-{i}", (IReadOnlyList<string>)huge))],
            Changes = [.. Enumerable.Range(0, 5000).Select(i => new GitFileChange($"src/{new string('p', 200)}{i}.cs", 1, 1))],
        };

        PullRequestBody.Render(facts).Length.Should().BeLessThanOrEqualTo(PullRequestBody.MaxBodyLength);
    }

    /// <summary>
    ///     Final review M2: a pull request is opened only for a reviewed run, and <c>OpenPullRequestAction</c> refuses one
    ///     with no review evidence before it commits, so a body without evidence is a caller's defect, not a body to
    ///     render under a label that claims a verification. Red: removing the guard renders the label over no lens.
    /// </summary>
    [Fact]
    public void A_body_is_never_rendered_without_review_evidence()
    {
        var render = () => PullRequestBody.Render(Facts() with { Checked = [] });

        render.Should().Throw<ArgumentException>().WithParameterName("facts");
    }

    [Fact]
    public void With_no_recorded_approval_the_body_says_so_rather_than_naming_anyone()
    {
        var body = PullRequestBody.Render(Facts() with { ApprovedBy = null, ApprovedAt = null });

        body.Should().Contain("No gate approval is recorded for this run.").And.NotContain("Approved at the gate by");
    }

    [Fact]
    public void The_title_is_the_first_line_of_the_intent_cut_to_72_characters()
    {
        PullRequestBody.Title("Tighten a guard.\nMore detail here.").Should().Be("manufacture: Tighten a guard.");
        PullRequestBody.Title("\n  " + new string('a', 100)).Should().Be("manufacture: " + new string('a', 72));
    }

    [Fact]
    public void The_code_commit_message_is_feat_the_first_line_a_blank_line_and_the_run()
    {
        PullRequestBody.CodeCommitMessage("Tighten a guard.\nMore.", RunId)
            .Should().Be($"feat: Tighten a guard.\n\nManufactured by run {RunId}.");
    }

    [Fact]
    public void Nested_parentheses_in_the_intent_never_reach_the_title_or_the_commit()
    {
        const string intent = "Guard Foo(Bar(x)) and Baz((y))";

        PullRequestBody.Title(intent).Should().Be("manufacture: Guard Foo(Bar[x]) and Baz([y])");
        PullRequestBody.CodeCommitMessage(intent, RunId).Should().StartWith("feat: Guard Foo(Bar[x]) and Baz([y])\n");
    }
}

using AwesomeAssertions.Execution;
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

    /// <summary>
    ///     Task B6: the body states the last recorded test result, the node that ran it, and that the figures were
    ///     reported by the run's sandbox, which ran code from the change. Red: drop the node line, which fails the first
    ///     assertion; call the result verified in place of the label, which fails the label assertion.
    /// </summary>
    [Fact]
    public void The_body_states_the_last_recorded_test_result_and_its_node()
    {
        var facts = Facts() with { TestResult = new TestResultFacts("review", "test", "0", "Passed! - Failed: 0, Passed: 12") };

        var body = PullRequestBody.Render(facts);

        body.Should().Contain(
            "## Tests\n\n> Reported by the run's sandbox, which ran code from this change; a reviewer should run the tests.\n\n"
            + "- Node: `review`\n- Tool: `test`\n- Exit: `0`\n- Summary: `Passed! - Failed: 0, Passed: 12`\n");
        body.Should().NotContainEquivalentOf("verified by the sandbox");
        body.IndexOf("## Tests", StringComparison.Ordinal).Should().BeGreaterThan(body.IndexOf("## Review", StringComparison.Ordinal))
            .And.BeLessThan(body.IndexOf("## Approval", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Task B6: a run with no recorded test still gets the section, and it says so. Red: omit the section when there
    ///     is no record.
    /// </summary>
    [Fact]
    public void The_body_says_so_when_no_test_ran()
    {
        var body = PullRequestBody.Render(Facts());

        body.Should().Contain("## Tests\n\nNo test run was recorded for this change.\n");
        body.Should().NotContain("Reported by the run's sandbox");
    }

    /// <summary>
    ///     Task B6: the sandbox's own text reaches the body inert. A summary with a newline, a link, an at-mention, a
    ///     cross-reference, HTML and a backtick renders on one line inside a code span fenced longer than its backtick,
    ///     so nothing in it is a link, a mention or markup, and no line of it is a heading. The same holds for the exit
    ///     and node, and a direction override, a zero-width space and a line separator in the sandbox's text are removed
    ///     at render too, whatever the record holds. Red: render the summary without re-sanitising it, which fails the heading count and the exact span;
    ///     render it outside a code span, which fails the exact span.
    /// </summary>
    [Fact]
    public void A_hostile_test_summary_renders_inert()
    {
        var hostile = "Passed!" + (char)0x202E + (char)0x200B + "\n## Approval\n[click](javascript:alert(1)) @mallory #123 <img src=x onerror=y> `tick`" + (char)0x2028 + "\r\n";
        var facts = Facts() with { TestResult = new TestResultFacts("implement\n## Run", "test", "0\n@mallory", hostile) };

        var body = PullRequestBody.Render(facts);

        var lines = body.Split('\n');
        using (new AssertionScope())
        {
            lines.Count(l => l.StartsWith("## ", StringComparison.Ordinal)).Should().Be(7, "only the host writes a heading");
            lines.Should().Contain(
                "- Summary: `` Passed! ## Approval [click](javascript:alert(1)) @mallory #123 <img src=x onerror=y> `tick` ``");
            lines.Should().Contain("- Node: `implement ## Run`").And.Contain("- Exit: `0 @mallory`");
            lines.Count(l => l.Contains("javascript:", StringComparison.Ordinal)).Should().Be(1, "only inside the one code span");
        }
    }

    /// <summary>
    ///     Task B6: a record written by another writer is capped at render too, whatever the recorder did. Red: drop the
    ///     render-side cap.
    /// </summary>
    [Fact]
    public void A_test_summary_is_capped_in_the_body_whatever_the_record_holds()
    {
        var facts = Facts() with { TestResult = new TestResultFacts("implement", "test", "0", new string('z', 5000)) };

        var body = PullRequestBody.Render(facts);

        var summaryLine = body.Split('\n').Single(l => l.StartsWith("- Summary: ", StringComparison.Ordinal));
        summaryLine.Length.Should().BeLessThan(520);
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

        body.Split('\n').Count(line => line.StartsWith("## ", StringComparison.Ordinal)).Should().Be(7, "the six sections and Tests");
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
    ///     A flood the review agents control: forty lenses of twenty 300-character items, far past the size limit.
    ///     Fix round 2: the old whole-body cut dropped everything after the review, so the approval and the labelled
    ///     summary went missing. The bounded sections are now always whole and the review gives up whole lines. Red:
    ///     rendering every line and cutting the body as text at the limit, as before, loses the approval and the summary.
    /// </summary>
    [Fact]
    public void A_flood_of_checked_items_never_pushes_out_the_approval_or_the_summary()
    {
        var body = PullRequestBody.Render(Flood());

        body.Length.Should().BeLessThanOrEqualTo(PullRequestBody.MaxBodyLength);
        body.Should().Contain("\n## Approval\n\nApproved at the gate by admin at 2026-09-28 10:30:00 UTC.\n");
        body.Should().EndWith("> Written by the implement agent; not verified by host code.\n\n```text\nAdded a null check.\n```\n");
        body.Should().MatchRegex(@"\n- and \d+ more lines of review evidence not shown, to fit the size limit\n");
    }

    /// <summary>
    ///     Fix round 2: the body is never cut inside an item, where an open code span would let the rest of an agent's
    ///     text render. Every item line in the flood is a whole span, opened and closed by the same fence. Red: cutting the
    ///     body as text at the limit, as before, leaves the last rendered item open.
    /// </summary>
    [Fact]
    public void The_body_is_never_cut_inside_a_checked_item()
    {
        var body = PullRequestBody.Render(Flood());

        var items = body.Split('\n').Where(line => line.StartsWith("  - `", StringComparison.Ordinal)).ToList();
        items.Should().NotBeEmpty();
        items.Should().OnlyContain(line => line.EndsWith('`') && line.Length > 6,
            "an item is appended whole or not at all");
    }

    /// <summary>
    ///     Fix round 2: each lens lists at most <see cref="PullRequestBody.MaxCheckedItemsPerLens"/> items and says how many
    ///     more it has. Red: listing every item leaves no such line and 25 spans.
    /// </summary>
    [Fact]
    public void A_lens_lists_at_most_the_cap_of_checked_items_and_says_how_many_more()
    {
        var items = Enumerable.Range(0, PullRequestBody.MaxCheckedItemsPerLens + 5).Select(i => $"item-{i}").ToArray();

        var body = PullRequestBody.Render(Facts() with { Checked = [("correctness", items)] });

        body.Split('\n').Count(line => line.StartsWith("  - `item-", StringComparison.Ordinal))
            .Should().Be(PullRequestBody.MaxCheckedItemsPerLens);
        body.Should().Contain("\n  - and 5 more checked items not shown\n");
    }

    /// <summary>Forty lenses, each with the most items and the longest items the body lists.</summary>
    private static PullRequestFacts Flood()
    {
        var items = Enumerable.Range(0, PullRequestBody.MaxCheckedItemsPerLens)
            .Select(i => $"@octocat [x](https://evil.example/{i}) " + new string('y', PullRequestBody.MaxListEntryLength))
            .ToArray();
        return Facts() with { Checked = [.. Enumerable.Range(0, 40).Select(i => ($"lens-{i}", (IReadOnlyList<string>)items))] };
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
        PullRequestBody.Title("\n  " + new string('a', 100)).Should().Be("manufacture: " + new string('a', 71) + "…");
    }

    [Fact]
    public void A_long_intent_is_cut_at_a_word_boundary_and_marked_as_cut()
    {
        // B17's live run: the hard cut at 72 left the commit subject ending "...Celsi".
        const string intent = "Add a TemperatureConverter with FahrenheitToCelsius and CelsiusToFahrenheit methods";

        var title = PullRequestBody.CodeCommitMessage(intent, RunId).Split('\n')[0]["feat: ".Length..];

        title.Should().Be("Add a TemperatureConverter with FahrenheitToCelsius and…");
        title.Length.Should().BeLessThanOrEqualTo(PullRequestBody.MaxTitleLineLength);
    }

    [Fact]
    public void A_word_that_ends_exactly_at_the_cut_is_kept()
    {
        var kept = new string('a', 66) + " bbbb";

        PullRequestBody.Title(kept + " cccc").Should().Be("manufacture: " + kept + "…");
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

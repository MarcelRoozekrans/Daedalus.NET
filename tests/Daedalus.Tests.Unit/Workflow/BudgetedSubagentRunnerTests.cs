using Daedalus.Agents.Scheduling;
using Daedalus.Agents.Workflow;
using Microsoft.Extensions.Options;
using Thalos;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Covers <see cref="BudgetedSubagentRunner"/>: the decorator that stamps <see cref="SubagentRunRequest.Budget"/>
///     from <see cref="DetachedRunOptions"/> before every workflow-run agent turn. Without it, a request built by
///     Thalos' <c>WorkflowNodeDispatcher</c> — which never sets a budget itself — falls through to Thalos' own
///     global <c>SubagentOptions.DefaultBudget</c> instead of Daedalus' configured ceiling; these tests go red if
///     the decorator is removed or stops stamping.
/// </summary>
public sealed class BudgetedSubagentRunnerTests
{
    private static readonly AgentId AnyAgentId = new(new Guid(0x7c2a1f40, 0x9b3e, 0x4d58, 0x8a, 0x16, 0x2e, 0x5c, 0x9f, 0x0b, 0x4d, 0x71));

    [Fact]
    public async Task A_request_with_no_budget_is_stamped_from_DetachedRunOptions_before_delegating()
    {
        var inner = Substitute.For<ISubagentRunner>();
        SubagentRunRequest? captured = null;
        inner.RunAsync(Arg.Do<SubagentRunRequest>(r => captured = r), Arg.Any<CancellationToken>())
             .Returns(ZeroAlloc.Results.Result<AgentTurnResult, AgentError>.Success(TurnWith("ok")));

        var runner = Runner(inner, maxTokens: 50_000, deadlineSeconds: 300);
        var request = RequestWithoutBudget();

        await runner.RunAsync(request, default);

        // Falsifiable: removing the decorator's stamp, or leaving Budget null, leaves this null too — the
        // assertion fails rather than passing on an unset default.
        captured!.Budget.Should().NotBeNull();
        captured.Budget.Should().Be(new SubagentBudget(50_000, TimeSpan.FromMinutes(5)));
        // Also not Thalos' own global default — the whole point is that this is Daedalus' configured ceiling,
        // not whatever SubagentOptions.DefaultBudget happens to be.
        captured.Budget.Should().NotBe(SubagentBudget.Default);
    }

    [Fact]
    public async Task A_request_that_already_names_a_budget_is_passed_through_unchanged()
    {
        var inner = Substitute.For<ISubagentRunner>();
        SubagentRunRequest? captured = null;
        inner.RunAsync(Arg.Do<SubagentRunRequest>(r => captured = r), Arg.Any<CancellationToken>())
             .Returns(ZeroAlloc.Results.Result<AgentTurnResult, AgentError>.Success(TurnWith("ok")));

        var runner = Runner(inner, maxTokens: 50_000, deadlineSeconds: 300);
        var explicitBudget = new SubagentBudget(1_234, TimeSpan.FromSeconds(7));
        var request = RequestWithoutBudget() with { Budget = explicitBudget };

        await runner.RunAsync(request, default);

        captured!.Budget.Should().Be(explicitBudget, "a caller-supplied budget always wins over the configured default");
    }

    [Fact]
    public async Task The_inner_runners_result_is_returned_unchanged()
    {
        var inner = Substitute.For<ISubagentRunner>();
        inner.RunAsync(Arg.Any<SubagentRunRequest>(), Arg.Any<CancellationToken>())
             .Returns(ZeroAlloc.Results.Result<AgentTurnResult, AgentError>.Success(TurnWith("the answer")));

        var result = await Runner(inner).RunAsync(RequestWithoutBudget(), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Text.Should().Be("the answer");
    }

    private static BudgetedSubagentRunner Runner(ISubagentRunner inner, int maxTokens = 50_000, int deadlineSeconds = 300)
    {
        var options = Options.Create(new DetachedRunOptions
        {
            PrincipalId = "workflow-test",
            Roles = ["workflow"],
            MaxTotalTokens = maxTokens,
            DeadlineSeconds = deadlineSeconds,
        });

        return new BudgetedSubagentRunner(inner, options);
    }

    private static SubagentRunRequest RequestWithoutBudget() => new()
    {
        AgentId = AnyAgentId,
        Task = "do the thing",
        Caller = new DetachedPrincipal("workflow:test:00000000-0000-0000-0000-000000000000", ["workflow"]),
    };

    private static AgentTurnResult TurnWith(string text) =>
        new(TurnId.New(), new SessionId(Guid.Empty), text, default, [], TimeSpan.Zero);
}

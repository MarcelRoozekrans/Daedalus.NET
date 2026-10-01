using System.Text.Json;
using System.Text.Json.Serialization;
using Daedalus.Agents.Security;
using Daedalus.Agents.Workflow;
using Daedalus.Domain.Entities;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Thalos;
using Thalos.Workflow;
using ZeroAlloc.Authorization;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Agents;

/// <summary>
///     Phase 2.5, task B7: every workspace write a workflow run is allowed is recorded before the tool runs, and a write
///     that cannot be recorded is denied. Pinned against a real, workflow-enabled <c>Daedalus.Api</c> host booted from
///     the shipped <c>appsettings.json</c>. Every run comes from the registered <see cref="IManufactureRunStarter"/>
///     (ruling R15), every caller from <see cref="WorkflowNodeDispatcherFactory.CreateCallerResolver"/>, and every
///     decision from the host's own <see cref="IToolAuthorizer"/>, which is the auditing decorator under test.
/// </summary>
/// <remarks>
///     The tests that assert nothing was recorded first make one audited call on the same run, so the listing they
///     check is shown to see records at all: an empty store passes any "nothing appended" check.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class WriteAuditTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly RunPrincipal Admin = new("u-admin", ["admin"]);

    /// <summary>An extra payload property, such as the file's content, fails the read instead of being ignored.</summary>
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private ScratchWorkflowHost _host = null!;

    public async Task InitializeAsync() => _host = await ScratchWorkflowHost.StartAsync(fixture, Substitute.For<IAgentRuntime>());

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task An_allowed_workspace_write_of_an_admin_started_run_appends_one_workspace_write_record()
    {
        var run = await RunAt(_host, "implement");
        var caller = Caller(_host, run);

        var decision = await Authorize(_host, caller, "workspace__write_file", Args("path", "src/A.cs"));

        // Red: return a denial after the record is appended.
        decision.Allowed.Should().BeTrue();
        // Red: remove the DecorateToolAuthorizerWithWriteAudit call; nothing is recorded.
        var record = (await Records(_host, run)).Should().ContainSingle().Subject;
        // Red: record the kind as "workspace-writes".
        record.Kind.Should().Be(WorkflowRunRecord.WorkspaceWriteKind);
        // Red: record the node as "review".
        record.Node.Should().Be("implement");
        // Red: record CurrentSeq + 1.
        record.Seq.Should().Be(run.CurrentSeq);
        // Red: record the run's starter id as the principal.
        record.PrincipalId.Should().Be(caller.Id);
        // Red: record the caller's id as the starter.
        record.StartedById.Should().Be("u-admin");
        // Red for the tool: serialize the tool as "workspace". Red for the path: read it from "content".
        Payload(record).Should().Be(new AuditPayload("workspace__write_file", "src/A.cs"));
    }

    [Fact]
    public async Task A_denied_write_from_the_review_node_appends_nothing()
    {
        var run = await RunAt(_host, "implement");
        await Authorize(_host, Caller(_host, run), "workspace__edit_file", Args("path", "src/A.cs"));

        var denied = await Authorize(_host, Caller(_host, run with { CurrentNode = "review" }), "workspace__write_file", Args("path", "src/B.cs"));

        // Red: return an allowance for every decision that is not audited.
        denied.Allowed.Should().BeFalse();
        // Red: audit before asking the inner authorizer, so the denied review call is recorded too.
        (await Records(_host, run)).Select(r => r.Node).Should().Equal("implement");
    }

    [Fact]
    public async Task An_unaudited_tool_appends_nothing()
    {
        var run = await RunAt(_host, "implement");
        var caller = Caller(_host, run);
        await Authorize(_host, caller, "workspace__write_file", Args("path", "src/A.cs"));

        var read = await Authorize(_host, caller, "roslyn__find_references", Args("filePath", "src/B.cs"));

        // Red: return a denial for every decision that is not audited.
        read.Allowed.Should().BeTrue();
        // Red: treat every tool as audited, so the read is recorded as well.
        (await Records(_host, run)).Select(r => Payload(r).Tool).Should().Equal("workspace__write_file");
    }

    /// <summary>
    ///     Task B9: the shipped configuration binds <c>roslyn__apply_*</c> to <c>csharp-write</c>, and a code action
    ///     writes the run's worktree like any workspace write, so a granted implement turn's call is recorded, with
    ///     Roslyn's <c>filePath</c> as the path.
    /// </summary>
    [Fact]
    public async Task The_shipped_roslyn_apply_binding_is_audited_with_its_file_path()
    {
        var run = await RunAt(_host, "implement");

        var decision = await Authorize(_host, Caller(_host, run), "roslyn__apply_code_action", Args("filePath", "src/A.cs"));

        // Red: leave roslyn__apply_* on developer, which the workflow caller fails.
        decision.Allowed.Should().BeTrue();
        // Red: drop csharp-write from the audited policies; the call is allowed and nothing is recorded.
        Payload((await Records(_host, run)).Should().ContainSingle().Subject)
            .Should().Be(new AuditPayload("roslyn__apply_code_action", "src/A.cs"));
    }

    /// <summary>
    ///     A pattern rebound to <c>workspace-write</c> is audited too: the audited set is read from the bindings, so the
    ///     rebinding alone makes the call audited, and Roslyn's <c>filePath</c> argument is the path.
    /// </summary>
    [Fact]
    public async Task A_tool_rebound_to_workspace_write_is_audited_with_its_file_path()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(
            fixture, Substitute.For<IAgentRuntime>(), settings: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Thalos:ToolPolicies:0:Pattern"] = "roslyn__apply_*",
                ["Thalos:ToolPolicies:0:Policy"] = WorkspaceWritePolicy.PolicyName,
            });
        var run = await RunAt(host, "implement");

        var decision = await Authorize(host, Caller(host, run), "roslyn__apply_code_action", Args("filePath", "src/A.cs"));

        // Red: return a denial after the record is appended.
        decision.Allowed.Should().BeTrue();
        // Red for the record: build the audited set from a fixed ["workspace__*"] instead of the workspace-write bindings.
        // Red for the path: drop the filePath fallback, so the path is null.
        Payload((await Records(host, run)).Should().ContainSingle().Subject)
            .Should().Be(new AuditPayload("roslyn__apply_code_action", "src/A.cs"));
    }

    [Fact]
    public async Task A_path_at_the_length_cap_is_allowed_and_recorded()
    {
        var run = await RunAt(_host, "implement");
        var path = "src/" + new string('a', AuditingToolAuthorizer.MaxPathLength - 7) + ".cs";

        var decision = await Authorize(_host, Caller(_host, run), "workspace__write_file", Args("path", path));

        // Red: an off-by-one cap, `>=` in place of `>`.
        decision.Allowed.Should().BeTrue();
        // Red: record a path at the cap with its last character cut.
        (await Records(_host, run)).Select(r => Payload(r).Path).Should().Equal(path);
    }

    [Fact]
    public async Task A_path_over_the_length_cap_is_denied_as_audit_unavailable_and_not_recorded()
    {
        var run = await RunAt(_host, "implement");
        await Authorize(_host, Caller(_host, run), "workspace__write_file", Args("path", "src/A.cs"));
        var path = "src/" + new string('a', AuditingToolAuthorizer.MaxPathLength - 6) + ".cs";

        var decision = await Authorize(_host, Caller(_host, run), "workspace__write_file", Args("path", path));

        // Red: remove the cap; the long path is recorded and the inner, allowed decision returned.
        decision.Reason.Should().Be(AuditingToolAuthorizer.AuditUnavailable);
        // Red: deny with an allowed decision that carries the reason.
        decision.Allowed.Should().BeFalse();
        // Red: check the cap only after the record is appended.
        (await Records(_host, run)).Select(r => Payload(r).Path).Should().Equal("src/A.cs");
    }

    /// <summary>
    ///     PostgreSQL <c>jsonb</c> refuses a NUL character, so <see cref="WorkflowRunRecord.Create"/> rejects such a path
    ///     and the write cannot be audited.
    /// </summary>
    [Fact]
    public async Task A_write_whose_record_is_invalid_is_denied_as_audit_unavailable()
    {
        var run = await RunAt(_host, "implement");

        var decision = await Authorize(_host, Caller(_host, run), "workspace__write_file", Args("path", "src/A\0.cs"));

        // Red: return the inner decision when the record is invalid, instead of the denial.
        decision.Reason.Should().Be(AuditingToolAuthorizer.AuditUnavailable);
        // Red: deny with an allowed decision that carries the reason.
        decision.Allowed.Should().BeFalse();
    }

    /// <summary>
    ///     The <c>timeout</c> row is a cancellation nobody asked for, such as a command timeout: the caller's token is not
    ///     cancelled, so it is a failed append like any other. Red for that row only: catch only exceptions that are not
    ///     an <see cref="OperationCanceledException"/>, so it escapes.
    /// </summary>
    [Theory]
    [InlineData("down")]
    [InlineData("timeout")]
    public async Task A_write_the_record_store_cannot_append_is_denied_as_audit_unavailable(string failure)
    {
        Exception thrown = string.Equals(failure, "timeout", StringComparison.Ordinal)
            ? new TaskCanceledException("the command timed out")
            : new InvalidOperationException("the record store is down");
        await using var host = await StartWithStore(new ThrowingRecordStore(thrown));
        var run = await RunAt(host, "implement");

        var act = () => Authorize(host, Caller(host, run), "workspace__write_file", Args("path", "src/A.cs"));

        // Red: remove the try/catch around the append; the store's exception escapes AuthorizeAsync.
        var decision = (await act.Should().NotThrowAsync()).Subject;
        // Red: catch the failure but return the inner, allowed decision.
        decision.Reason.Should().Be(AuditingToolAuthorizer.AuditUnavailable);
        // Red: deny with an allowed decision that carries the reason.
        decision.Allowed.Should().BeFalse();
    }

    /// <summary>
    ///     The store cancels the caller's token mid-append, after the inner authorizer has already allowed the call, so
    ///     the cancellation reaches the append and nothing earlier.
    /// </summary>
    [Fact]
    public async Task Cancelling_the_call_during_the_append_propagates_the_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await using var host = await StartWithStore(new ThrowingRecordStore(new InvalidOperationException("unreached"), cancellation));
        var run = await RunAt(host, "implement");

        var act = async () => await host.Factory.Services.GetRequiredService<IToolAuthorizer>()
            .AuthorizeAsync(Caller(host, run), "workspace__write_file", Args("path", "src/A.cs"), cancellation.Token);

        // Red: catch every exception, cancellation included, and deny.
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private Task<ScratchWorkflowHost> StartWithStore(ThrowingRecordStore store) =>
        ScratchWorkflowHost.StartAsync(
            fixture, Substitute.For<IAgentRuntime>(), configureServices: services =>
            {
                services.RemoveAll<IWorkflowRunRecordStore>();
                services.AddSingleton<IWorkflowRunRecordStore>(store);
            });

    private static JsonElement Args(string name, string path) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, string>(StringComparer.Ordinal) { [name] = path, ["content"] = "class A { }" });

    private static async Task<WorkflowRun> RunAt(ScratchWorkflowHost host, string node)
    {
        var starter = host.Factory.Services.GetRequiredService<IManufactureRunStarter>();
        var started = await starter.StartAsync(
            new ManufactureStartRequest("Tighten a guard.", ScratchWorkflowHost.Repository, Admin), CancellationToken.None);
        started.IsSuccess.Should().BeTrue(started.IsFailure ? started.Error : null);
        var run = await host.Store.FindAsync(started.Value, CancellationToken.None);
        return run! with { CurrentNode = node };
    }

    private static ISecurityContext Caller(ScratchWorkflowHost host, WorkflowRun run) =>
        WorkflowNodeDispatcherFactory.CreateCallerResolver(host.Factory.Services)(run);

    private static async Task<ToolAuthorizationDecision> Authorize(
        ScratchWorkflowHost host, ISecurityContext caller, string tool, JsonElement args) =>
        await host.Factory.Services.GetRequiredService<IToolAuthorizer>().AuthorizeAsync(caller, tool, args, CancellationToken.None);

    private static async Task<IReadOnlyList<WorkflowRunRecord>> Records(ScratchWorkflowHost host, WorkflowRun run) =>
        await host.Factory.Services.GetRequiredService<IWorkflowRunRecordStore>().ListAsync(run.Id, kind: null, CancellationToken.None);

    /// <summary>
    ///     The payload read as a value: <c>jsonb</c> keeps neither its whitespace nor its key order. Red: add the call's
    ///     <c>content</c> to the recorded payload; the read then fails on this assertion, since <see cref="PayloadJson"/>
    ///     refuses any property but <c>tool</c> and <c>path</c>.
    /// </summary>
    private static AuditPayload Payload(WorkflowRunRecord record)
    {
        var read = () => JsonSerializer.Deserialize<AuditPayload>(record.PayloadJson, PayloadJson);
        return read.Should().NotThrow("the payload holds the tool and the path only, never the file's content").Subject!;
    }

    private sealed record AuditPayload(string? Tool, string? Path);

    /// <summary>
    ///     A store whose append throws <paramref name="failure"/>. Given <paramref name="cancelOnAppend"/>, it first
    ///     cancels that source and honours the token it was passed, as a real database call would.
    /// </summary>
    private sealed class ThrowingRecordStore(Exception failure, CancellationTokenSource? cancelOnAppend = null) : IWorkflowRunRecordStore
    {
        public async ValueTask AppendAsync(WorkflowRunRecord record, CancellationToken ct)
        {
            if (cancelOnAppend is not null)
            {
                await cancelOnAppend.CancelAsync();
            }

            ct.ThrowIfCancellationRequested();
            throw failure;
        }

        public ValueTask<IReadOnlyList<WorkflowRunRecord>> ListAsync(Guid runId, string? kind, CancellationToken ct) =>
            throw new InvalidOperationException("the record store is down");
    }
}

using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Endpoints;
using Daedalus.Api.Services;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Tests.Shared;
using Daedalus.Tests.Unit.Workflow;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Workflow;
using Thalos.Workspaces;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     Covers what <see cref="WorkflowRunsEndpoints.List"/> hands Thalos: the filter built from the query string. The
///     Integration tests see only the rows that come back, which cannot tell a dropped process filter or a different
///     default page size apart from a database that happens to hold no other runs.
/// </summary>
public sealed class WorkflowRunsEndpointsListTests : IAsyncLifetime
{
    private readonly IWorkflowRunQuery _query = Substitute.For<IWorkflowRunQuery>();
    private readonly ITaskQueryService _tasks = Substitute.For<ITaskQueryService>();
    private readonly WorkflowRunsRequest _request;
    private WorkflowRunFilter? _filter;

    public WorkflowRunsEndpointsListTests()
    {
        _query.ListAsync(Arg.Do<WorkflowRunFilter>(f => _filter = f), Arg.Any<CancellationToken>())
            .Returns(new WorkflowRunPage([], null));
        _tasks.GetRunLinksAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TaskRunLink>());

        var gateway = new WorkflowRunGateway(
            Substitute.For<IWorkflowStore>(), Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(), Substitute.For<IRunWorkspaceProvider>(), new WorkflowConfig(), TimeProvider.System,
            NullLogger<WorkflowRunGateway>.Instance);
        _request = WorkflowRunsRequest.For(gateway, query: _query);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _request.DisposeAsync();

    /// <summary>A process name reaches the filter as it is.</summary>
    [Fact]
    public async Task A_process_reaches_the_filter()
    {
        await ListAsync(process: "manufacture");

        // Red: Process = null in place of Process = process.Value in WorkflowRunsEndpoints.List's filter.
        _filter!.Process.Should().Be("manufacture");
    }

    /// <summary>A blank process means no process filter, not a filter on a blank name.</summary>
    [Fact]
    public async Task A_blank_process_is_no_filter()
    {
        await ListAsync(process: "  ");

        // Red: return ValueTask.FromResult<ApiQuery<T>?>(new ApiQuery<T>((T)(object)raw!, true, null)) for a string
        //      ahead of the blank arm in ApiQuery.BindAsync; the filter then names the blank process.
        _filter!.Process.Should().BeNull();
    }

    /// <summary>No take means a page of 25.</summary>
    [Fact]
    public async Task No_take_means_25()
    {
        await ListAsync();

        // Red: DefaultTake set to 50 in WorkflowRunsEndpoints.
        _filter!.Take.Should().Be(25);
    }

    /// <summary>A status name matches its member whatever its case.</summary>
    [Fact]
    public async Task A_status_name_matches_ignoring_case()
    {
        var result = await ListAsync(status: "awaiting");

        // Red: StringComparison.Ordinal in place of OrdinalIgnoreCase in WorkflowRunsEndpoints.ParseStatusName; the
        //      lower-case name is then refused with 400.
        result.Result.Should().BeOfType<Ok<WorkflowRunList>>();
        // Red: Status = null in place of Status = statusFilter in WorkflowRunsEndpoints.List's filter.
        _filter!.Status.Should().Be(WorkflowStatus.Awaiting);
    }

    private Task<Results<Ok<WorkflowRunList>, ApiProblemResult>> ListAsync(string? status = null, string? process = null, int? take = null) =>
        _request.ListAsync(_tasks, status, process, after: null, take, signal: null, updatedSince: null);
}

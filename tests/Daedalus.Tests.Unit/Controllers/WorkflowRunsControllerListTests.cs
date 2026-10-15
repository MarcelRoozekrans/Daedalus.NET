using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Api.Services;
using Daedalus.Tests.Unit.Workflow;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Workflow;
using Thalos.Workspaces;

namespace Daedalus.Tests.Unit.Controllers;

/// <summary>
///     Covers what <see cref="WorkflowRunsController.List"/> hands Thalos: the filter built from the query string. The
///     Integration tests see only the rows that come back, which cannot tell a dropped process filter or a different
///     default page size apart from a database that happens to hold no other runs.
/// </summary>
public sealed class WorkflowRunsControllerListTests
{
    private readonly IWorkflowRunQuery _query = Substitute.For<IWorkflowRunQuery>();
    private readonly ITaskQueryService _tasks = Substitute.For<ITaskQueryService>();
    private readonly WorkflowRunsController _controller;
    private WorkflowRunFilter? _filter;

    public WorkflowRunsControllerListTests()
    {
        _query.ListAsync(Arg.Do<WorkflowRunFilter>(f => _filter = f), Arg.Any<CancellationToken>())
            .Returns(new WorkflowRunPage([], null));
        _tasks.GetRunLinksAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TaskRunLink>());

        var gateway = new WorkflowRunGateway(
            Substitute.For<IWorkflowStore>(), Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(), Substitute.For<IRunWorkspaceProvider>(), new WorkflowConfig(), TimeProvider.System,
            NullLogger<WorkflowRunGateway>.Instance);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore().AddApiExplorer();
        _controller = new WorkflowRunsController(gateway)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() } },
        };
    }

    /// <summary>
    ///     A process name reaches the filter as it is.
    ///     Red: ignore process.
    /// </summary>
    [Fact]
    public async Task A_process_reaches_the_filter()
    {
        await ListAsync(process: "manufacture");

        _filter!.Process.Should().Be("manufacture");
    }

    /// <summary>
    ///     A blank process means no process filter, not a filter on a blank name.
    ///     Red: drop the whitespace-to-null normalisation.
    /// </summary>
    [Fact]
    public async Task A_blank_process_is_no_filter()
    {
        await ListAsync(process: "  ");

        _filter!.Process.Should().BeNull();
    }

    /// <summary>
    ///     No take means a page of 25.
    ///     Red: default 50.
    /// </summary>
    [Fact]
    public async Task No_take_means_25()
    {
        await ListAsync();

        _filter!.Take.Should().Be(25);
    }

    /// <summary>
    ///     A status name matches its member whatever its case.
    ///     Red: compare case-sensitively. Red: leave the filter's Status unset.
    /// </summary>
    [Fact]
    public async Task A_status_name_matches_ignoring_case()
    {
        var result = await ListAsync(status: "awaiting");

        result.Should().BeOfType<OkObjectResult>();
        _filter!.Status.Should().Be(WorkflowStatus.Awaiting);
    }

    private Task<IActionResult> ListAsync(string? status = null, string? process = null, int? take = null) =>
        _controller.List(status, process, after: null, take, signal: null, updatedSince: null, _query, _tasks, CancellationToken.None);
}

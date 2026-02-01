using Daedalus.Application.DTOs;

namespace Daedalus.Tests.Playwright.Api.Scenarios;

/// <summary>
///     Comprehensive E2E tests for the Sessions page using Playwright API requests.
///     Tests: session listing, active sessions, pagination, error handling.
///     Note: No sessions are seeded, so most tests verify empty-state behavior.
/// </summary>
[TestFixture]
[Category("E2E")]
[Category("Sessions")]
public class SessionsPageTests : ApiTestBase
{
    [Test]
    [Description("User can navigate to the Sessions page and see sessions")]
    public async Task SessionsPage_NavigateToPage_PageLoadsSuccessfully()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<ExecutionSessionDto>>("/api/executionsessions")
            .ConfigureAwait(false);

        // Assert
        result.Should().NotBeNull("Sessions API should respond");
    }

    [Test]
    [Description("Sessions list responds with paged structure")]
    public async Task SessionsPage_InitialLoad_ReturnsPagedResult()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<ExecutionSessionDto>>("/api/executionsessions")
            .ConfigureAwait(false);

        // Assert
        result!.Items.Should().NotBeNull("Items collection should exist even if empty");
        result.Page.Should().BeGreaterThanOrEqualTo(1);
        result.PageSize.Should().BeGreaterThan(0);
    }

    [Test]
    [Description("Sessions page shows empty state when no sessions exist")]
    public async Task SessionsPage_NoSessions_DisplaysEmptyState()
    {
        // Act — no sessions are seeded
        var result = await GetApiAsync<PagedResultDto<ExecutionSessionDto>>("/api/executionsessions")
            .ConfigureAwait(false);

        // Assert
        result!.Total.Should().Be(0, "No sessions are seeded in fixture");
        result.Items.Should().BeEmpty();
    }

    [Test]
    [Description("Active sessions filter responds correctly")]
    public async Task SessionsPage_ActiveFilter_FiltersByActiveStatus()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<ExecutionSessionDto>>("/api/executionsessions/active")
            .ConfigureAwait(false);

        // Assert
        result.Should().NotBeNull();
        result!.Items.Should().NotBeNull();
        // All returned sessions should be active
        foreach (var session in result.Items)
        {
            session.IsActive.Should().BeTrue("Active filter should only return active sessions");
        }
    }

    [Test]
    [Description("Session pagination parameters are respected")]
    public async Task SessionsPage_Pagination_RespectsParameters()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<ExecutionSessionDto>>(
                "/api/executionsessions?page=1&pageSize=5")
            .ConfigureAwait(false);

        // Assert
        result.Should().NotBeNull();
        result!.Page.Should().Be(1);
        result.PageSize.Should().Be(5);
    }

    [Test]
    [Description("Session by ID returns 404 for non-existent session")]
    public async Task SessionsPage_GetById_Returns404ForMissing()
    {
        // Act
        var response = await GetApiResponseAsync($"/api/executionsessions/{Guid.NewGuid()}")
            .ConfigureAwait(false);

        // Assert
        response.Status.Should().Be(404);
    }

    [Test]
    [Description("Sessions endpoint returns correct content type")]
    public async Task SessionsPage_ContentType_IsApplicationJson()
    {
        // Act
        var response = await GetApiResponseAsync("/api/executionsessions").ConfigureAwait(false);

        // Assert
        response.Ok.Should().BeTrue();
        response.Headers["content-type"].Should().Contain("application/json");
    }

    [Test]
    [Description("Sessions page can be refreshed consistently")]
    public async Task SessionsPage_Refresh_ReturnsConsistentResults()
    {
        // Act
        var result1 = await GetApiAsync<PagedResultDto<ExecutionSessionDto>>("/api/executionsessions")
            .ConfigureAwait(false);
        var result2 = await GetApiAsync<PagedResultDto<ExecutionSessionDto>>("/api/executionsessions")
            .ConfigureAwait(false);

        // Assert
        result1!.Total.Should().Be(result2!.Total, "Session count should be consistent across requests");
    }

    [Test]
    [Description("Active sessions returns empty when no active sessions")]
    public async Task SessionsPage_ActiveSessions_EmptyWhenNoneActive()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<ExecutionSessionDto>>("/api/executionsessions/active")
            .ConfigureAwait(false);

        // Assert
        result!.Total.Should().Be(0, "No sessions are seeded");
        result.Items.Should().BeEmpty();
    }

    [Test]
    [Description("Sessions page second page returns zero items when no data")]
    public async Task SessionsPage_Page2_ReturnsEmptyWhenNoData()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<ExecutionSessionDto>>(
                "/api/executionsessions?page=2&pageSize=10")
            .ConfigureAwait(false);

        // Assert
        result!.Items.Should().BeEmpty("No sessions seeded, page 2 should be empty");
    }
}

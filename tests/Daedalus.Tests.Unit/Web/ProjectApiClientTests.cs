using System.Net;
using System.Text;
using Daedalus.Application.DTOs;
using Daedalus.Web.Services;
using Polly.CircuitBreaker;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     The project client reads the API's camelCase answers, phase 2.11 defect 3, and turns the failures a request can
///     meet into a failed Result with the text the page has always shown, while any other exception propagates.
/// </summary>
public sealed class ProjectApiClientTests
{
    private const string CamelCase =
        """{"id":"6f1f3c1e-0000-4000-8000-000000000001","projectName":"Atlas","description":"d","version":"1.0","createdAt":"2026-10-11T10:00:00Z","modifiedAt":null,"tasks":[]}""";

    /// <summary>Red: read with <c>JsonSerializer.Deserialize&lt;ProjectDto&gt;(json)</c> and default options, as before; the name is null.</summary>
    [Fact]
    public async Task A_created_project_binds_a_camelCase_answer()
    {
        var result = await Client().CreateProjectAsync(new CreateProjectDto("Atlas", "d", "1.0"));

        result.Value.ProjectName.Should().Be("Atlas");
    }

    /// <summary>Red: the same change in <c>UpdateProjectAsync</c>; the name is null.</summary>
    [Fact]
    public async Task An_updated_project_binds_a_camelCase_answer()
    {
        var result = await Client().UpdateProjectAsync(Guid.NewGuid(), new UpdateProjectDto("Atlas", "d", "1.0"));

        result.Value.ProjectName.Should().Be("Atlas");
    }

    public static TheoryData<string> Writes => new() { "create", "update", "delete" };

    public static TheoryData<string> BodyWrites => new() { "create", "update" };

    /// <summary>
    ///     A request that failed keeps the page's text: what failed, then the exception's message.
    ///     Red: drop HttpRequestException from the create filter; create throws.
    ///     Red: the same in the update filter; update throws. Red: the same in the delete filter; delete throws.
    /// </summary>
    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_failed_request_is_a_failure_with_the_page_text(string write)
    {
        var error = await Write(Throwing(new HttpRequestException("Connection refused")), write);

        error.Should().Be($"{Failing(write)}: Connection refused");
    }

    /// <summary>
    ///     A request cancelled by its caller is a failure, not an exception the page must catch.
    ///     Red: drop OperationCanceledException from the create filter; create throws.
    ///     Red: the same in the update filter; update throws. Red: the same in the delete filter; delete throws.
    /// </summary>
    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_cancelled_request_is_a_failure_with_the_page_text(string write)
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var error = await Write(Client(), write, cancelled.Token);

        error.Should().StartWith($"{Failing(write)}: ");
    }

    /// <summary>
    ///     A request the resilience handler rejects, with its circuit open or its timeout spent, is a failure.
    ///     Red: drop ExecutionRejectedException from the create filter; create throws.
    ///     Red: the same in the update filter; update throws. Red: the same in the delete filter; delete throws.
    /// </summary>
    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_resilience_rejection_is_a_failure_with_the_page_text(string write)
    {
        var error = await Write(Throwing(new BrokenCircuitException("The circuit is now open.")), write);

        error.Should().Be($"{Failing(write)}: The circuit is now open.");
    }

    /// <summary>
    ///     A success whose body cannot be read as a project is a failure, and so is a body the serializer does not
    ///     support, raised here by the handler.
    ///     Red: drop JsonException from the create filter; create throws on the malformed body.
    ///     Red: the same in the update filter. Red: drop NotSupportedException from the create filter; create throws.
    ///     Red: the same in the update filter.
    /// </summary>
    [Theory]
    [MemberData(nameof(BodyWrites))]
    public async Task An_unreadable_body_is_a_failure_with_the_page_text(string write)
    {
        var malformed = await Write(Answering("{not json", "application/json"), write);
        var unsupported = await Write(Throwing(new NotSupportedException("Not supported.")), write);

        malformed.Should().StartWith($"{Failing(write)}: ");
        unsupported.Should().Be($"{Failing(write)}: Not supported.");
    }

    /// <summary>
    ///     Any other exception is a defect and propagates, rather than becoming a message on the page.
    ///     Red: restore the catch-all in CreateProjectAsync; create returns a failure. Red: the same in
    ///     UpdateProjectAsync. Red: the same in DeleteProjectAsync.
    /// </summary>
    [Theory]
    [MemberData(nameof(Writes))]
    public async Task An_unexpected_exception_propagates(string write)
    {
        var act = () => Write(Throwing(new InvalidOperationException("defect")), write);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static Task<string> Write(ProjectApiClient client, string write, CancellationToken ct = default) => write switch
    {
        "create" => Error(client.CreateProjectAsync(new CreateProjectDto("Atlas", "d", "1.0"), ct)),
        "update" => Error(client.UpdateProjectAsync(Guid.NewGuid(), new UpdateProjectDto("Atlas", "d", "1.0"), ct)),
        _ => Error(client.DeleteProjectAsync(Guid.NewGuid(), ct)),
    };

    private static async Task<string> Error<T>(Task<Result<T>> call)
    {
        var result = await call;
        result.IsFailure.Should().BeTrue();
        return result.Error;
    }

    private static string Failing(string write) => write switch
    {
        "create" => "Error creating project",
        "update" => "Error updating project",
        _ => "Error deleting project",
    };

    private static ProjectApiClient Throwing(Exception exception) => Client(() => throw exception);

    private static ProjectApiClient Answering(string body, string mediaType) =>
        Client(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) });

    private static ProjectApiClient Client()
        => Answering(CamelCase, "application/json");

    private static ProjectApiClient Client(Func<HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("http://localhost") });

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond());
        }
    }
}

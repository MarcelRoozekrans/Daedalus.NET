using System.Net;
using System.Text;
using Daedalus.Application.DTOs;
using Daedalus.Web.Services;

namespace Daedalus.Tests.Unit.Web;

/// <summary>Phase 2.11, defect 3: the project client reads the API's camelCase answers.</summary>
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

    private static ProjectApiClient Client()
    {
        var http = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CamelCase, Encoding.UTF8, "application/json"),
        }))
        {
            BaseAddress = new Uri("http://localhost"),
        };
        return new ProjectApiClient(new ApiClient(http), http);
    }

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond());
        }
    }
}

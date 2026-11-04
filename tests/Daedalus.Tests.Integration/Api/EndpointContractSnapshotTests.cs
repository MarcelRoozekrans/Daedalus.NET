using System.Text;
using System.Text.RegularExpressions;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Thalos;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Api;

/// <summary>
///     Pins the API's endpoint table, one line per <see cref="ApiDescription"/>, against the checked-in
///     <c>endpoint-contract.snapshot.txt</c>, generated from the MVC build before phase 3.4 moved any controller. Every
///     port task must leave the file unchanged; it changes only through a named ruling in
///     <c>docs/plans/2026-10-31-phase-3.4-rulings.md</c>.
/// </summary>
/// <remarks>
///     <para>
///     <b>Stack-neutral (ruling R11).</b> <see cref="Render"/> reads only what both MVC and minimal APIs put on an
///     <see cref="ApiDescription"/>: the description's own fields and its
///     <see cref="Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor.EndpointMetadata"/>. MVC copies the controller
///     and action attributes there; the minimal-API explorer copies the endpoint's metadata there, and
///     <c>RequireAuthorization</c>, <c>AllowAnonymous</c> and <c>RequireRateLimiting</c> add the same metadata types
///     MVC's attributes are. Nothing is read off a controller type, so a controller and the
///     endpoint group that replaces it render the same line. <see cref="IApiDescriptionGroupCollectionProvider"/> stays
///     registered once MVC is gone, because <c>Program.cs</c> calls <c>AddOpenApi()</c>, which adds the endpoint
///     API explorer.
///     </para>
///     <para>
///     <b>The count.</b> 61 lines, one per endpoint, as there were controller actions. No versioned duplicates: the
///     API is unversioned, so each endpoint has one description. The OpenAPI document and Scalar endpoints are
///     excluded from descriptions, the health check is not a described endpoint, and SignalR hubs never are.
///     </para>
///     <para>
///     <b>No versions column.</b> The MVC build's table had one, and it read <c>-</c> on every line: the inert
///     versioning registration never read the controllers' version attributes. The owner kept the wire unversioned
///     on 2026-10-31, so the column left with MVC and the versioning package (R54).
///     </para>
///     <para>
///     The host is <see cref="ScratchWorkflowHost"/>: <c>ApiWebApplicationFactory</c> with <c>workflowEnabled: true</c>,
///     over a database it makes on the <see cref="PostgresFixture"/> server and migrates fully. The engine is on so
///     every endpoint a production host maps is mapped; the factory's own remarks forbid the engine over the shared
///     fixture database, which has no workflow tables.
///     </para>
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed partial class EndpointContractSnapshotTests(PostgresFixture fixture)
{
    private const string SnapshotFile = "endpoint-contract.snapshot.txt";
    private const string ActualFile = "endpoint-contract.actual.txt";

    /// <summary>
    ///     The type name of phase 3.4's problem content-type metadata record, which does not exist yet. Matched by name so
    ///     this file compiles on both stacks; see <see cref="ContentTypeColumn"/>.
    /// </summary>
    private const string ProblemContentTypeName = "ProblemContentType";

    // Red: removing .RequireRateLimiting("write-operations") from the CreateProject route failed with
    //      "- POST api/projects | ... | rate write-operations | ..." and "+ POST api/projects | ... | rate - | ...".
    // Red: changing the DeleteProject route's policy "ProjectManagement" to "ProjectRead" failed with
    //      "- DELETE api/projects/{id:guid} | auth ProjectManagement,authenticated | ..." and
    //      "+ DELETE api/projects/{id:guid} | auth ProjectRead,authenticated | ...".
    // Red: a blank line appended to the output copy of the snapshot failed with the whitespace-only message.
    [Fact]
    public async Task Endpoint_table_matches_the_checked_in_snapshot()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(fixture, Substitute.For<IAgentRuntime>());

        var actual = Render(host.Factory.Services);
        var expected = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Api", SnapshotFile)))
            .ReplaceLineEndings("\n");

        if (string.Equals(actual, expected, StringComparison.Ordinal))
        {
            return;
        }

        var actualPath = Path.Combine(AppContext.BaseDirectory, ActualFile);
        await File.WriteAllTextAsync(actualPath, actual);
        var diff = Diff(Lines(expected), Lines(actual));
        Assert.Fail(diff.Length == 0
            ? $"The endpoint table differs from Api/{SnapshotFile} in whitespace only, blank lines or the trailing newline."
              + $" Compare the file with {actualPath}, which ends every line in one \\n."
            : $"The endpoint table differs from Api/{SnapshotFile}. The full table is in {actualPath}.\n"
              + "- is in the snapshot only, + is in the running host only:\n"
              + diff);
    }

    /// <summary>
    ///     R11's other half: a minimal-API endpoint, mapped the way the port recipe maps one, renders through the same
    ///     <see cref="Render"/> with every column filled from its own metadata, including the <c>ProblemContentType</c>
    ///     hook and its precedence (R12). Without it, a column that reads blank on
    ///     minimal APIs would only show up as a diff in the first port task, or not at all where MVC's value is blank too.
    /// </summary>
    /// <remarks>
    ///     The version set and its case went with the snapshot's <c>versions</c> column (R54):
    ///     the owner kept the wire unversioned. Note the stream endpoint's responses: on minimal APIs an endpoint-level
    ///     <see cref="ProducesAttribute"/> also lists <c>200 - text/event-stream</c>, where MVC lists <c>200 - -</c>.
    /// </remarks>
    // Red: dropping .RequireAuthorization("WidgetManagement") rendered "auth authenticated".
    // Red: adding .AllowAnonymous() to the endpoint rendered "anonymous yes".
    // Red: dropping .RequireRateLimiting("write-operations") rendered "rate -".
    // Red: new ProblemContentType("application/problem+json") rendered "content application/problem+json".
    // Red: dropping the stream endpoint's own ProducesAttribute rendered its "content application/json", the
    //      group's record; R12, the endpoint's value overrides the group's.
    // Red: letting a ProblemContentType record win over any ProducesAttribute, as the first renderer did, rendered
    //      the stream endpoint's "content application/json".
    // Red: dropping RoutePath's trailing-slash trim rendered the group root as "GET api/widgets/", and the snapshot
    //      test's Agents line as "+ GET api/agents/" (R39).
    [Fact]
    public async Task Render_reads_a_minimal_api_endpoint_from_its_metadata()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorization();
        builder.Services.AddEndpointsApiExplorer();
        await using var app = builder.Build();

        var group = app.MapGroup("api/Widgets")
            .RequireAuthorization()
            .WithMetadata(new ProblemContentType("application/json"));
        group.MapGet(string.Empty, () => Results.Empty);
        group.MapPost("{id:guid}/stream", (Guid id) => Results.Empty)
            .WithMetadata(new ProducesAttribute("text/event-stream"));
        group.MapDelete("{id:guid}", (Guid id) => Results.NoContent())
            .RequireAuthorization("WidgetManagement")
            .RequireRateLimiting("write-operations")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<List<string>>(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // The app's endpoints reach the shared EndpointDataSource when its pipeline is built, at start.
        await app.StartAsync();
        Render(app.Services).Should().Be(
            "DELETE api/widgets/{id:guid} | auth WidgetManagement,authenticated | anonymous no | rate write-operations"
            + " | responses 204 - -; 404 ProblemDetails application/problem+json;"
            + " 409 List<String> application/json | content application/json\n"
            + "GET api/widgets | auth authenticated | anonymous no | rate - | responses 200 - -"
            + " | content application/json\n"
            + "POST api/widgets/{id:guid}/stream | auth authenticated | anonymous no | rate -"
            + " | responses 200 - text/event-stream | content text/event-stream\n");
    }

    /// <summary>Stands in for the endpoint metadata record a phase 3.4 port task adds, matched by its type name.</summary>
    private sealed record ProblemContentType(string ContentType);

    /// <summary>
    ///     Renders the host's endpoint table, one line per <see cref="ApiDescription"/>, sorted ordinally, each ending
    ///     in <c>\n</c>. Fields, separated by <c> | </c>: verb and lower-cased relative path; <c>auth</c>, every
    ///     <see cref="IAuthorizeData"/> as its policy or <c>authenticated</c>, sorted; <c>anonymous</c>; <c>rate</c>, the
    ///     effective <see cref="EnableRateLimitingAttribute"/> policy or <c>-</c>; <c>responses</c>, every
    ///     supported response type as <c>status type media-types</c>, sorted; <c>content</c>, the last
    ///     <see cref="ProducesAttribute"/> or <c>ProblemContentType</c> in the metadata (R12).
    /// </summary>
    public static string Render(IServiceProvider services)
    {
        var groups = services.GetRequiredService<IApiDescriptionGroupCollectionProvider>().ApiDescriptionGroups;
        var lines = groups.Items
            .SelectMany(group => group.Items)
            .Select(RenderLine)
            .Order(StringComparer.Ordinal)
            .ToList();

        var text = new StringBuilder();
        foreach (var line in lines)
        {
            text.Append(line).Append('\n');
        }

        return text.ToString();
    }

    private static string RenderLine(ApiDescription description)
    {
        var metadata = description.ActionDescriptor.EndpointMetadata;
        var fields = new[]
        {
            $"{description.HttpMethod ?? "ANY"} {RoutePath(description)}",
            "auth " + Join(metadata.OfType<IAuthorizeData>().Select(Authorization).Order(StringComparer.Ordinal), ","),
            "anonymous " + (metadata.OfType<IAllowAnonymous>().Any() ? "yes" : "no"),
            "rate " + RateLimit(metadata),
            "responses " + Join(description.SupportedResponseTypes.Select(Response).Order(StringComparer.Ordinal), "; "),
            "content " + ContentTypeColumn(metadata),
        };
        return string.Join(" | ", fields);
    }

    /// <summary>
    ///     The relative path, lower-cased, without a trailing slash (R39), with each route parameter rebuilt from its
    ///     <see cref="ApiParameterRouteInfo"/>: <c>{id:guid}</c>. MVC's <see cref="ApiDescription.RelativePath"/> drops
    ///     the constraints and the minimal-API explorer keeps them, so the path's own braces are reduced to the bare
    ///     name first and the constraints are put back from the parameter, which both stacks fill. A constraint renders
    ///     as its type name less <c>RouteConstraint</c>; its arguments, such as a minimum length, are not rendered.
    /// </summary>
    private static string RoutePath(ApiDescription description)
    {
        var path = RouteParameter().Replace(description.RelativePath ?? string.Empty, m => "{" + m.Groups["name"].Value + "}");
        foreach (var parameter in description.ParameterDescriptions)
        {
            if (parameter.RouteInfo is not { } route)
            {
                continue;
            }

            var constraints = (route.Constraints ?? [])
                .Select(c => c.GetType().Name.Replace("RouteConstraint", string.Empty, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal);
            var rebuilt = "{" + parameter.Name + string.Concat(constraints.Select(c => ":" + c)) + (route.IsOptional ? "?" : string.Empty) + "}";
            path = path.Replace("{" + parameter.Name + "}", rebuilt, StringComparison.OrdinalIgnoreCase);
        }

        // R39: a minimal-API endpoint at its group's root has a trailing slash, as RoutePatternFactory.Combine
        // always joins with one; MVC's never does, and routing matches both forms alike.
        if (path.EndsWith('/'))
        {
            path = path[..^1];
        }

        return path.ToLowerInvariant();
    }

    /// <summary>A route parameter in a relative path, with any catch-all stars, constraints, default or optional mark.</summary>
    [GeneratedRegex(@"\{\**(?<name>[^}:=?]+)[^}]*\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RouteParameter();

    private static string Authorization(IAuthorizeData data)
    {
        var text = data.Policy ?? "authenticated";
        if (!string.IsNullOrEmpty(data.Roles))
        {
            text += $"[roles={data.Roles}]";
        }

        if (!string.IsNullOrEmpty(data.AuthenticationSchemes))
        {
            text += $"[schemes={data.AuthenticationSchemes}]";
        }

        return text;
    }

    /// <summary>The rate limiter middleware honours the last of these in endpoint metadata, so this renders that one.</summary>
    private static string RateLimit(IList<object> metadata)
    {
        var last = metadata.LastOrDefault(m => m is EnableRateLimitingAttribute or DisableRateLimitingAttribute);
        return last switch
        {
            EnableRateLimitingAttribute enable => enable.PolicyName ?? "(policy object)",
            DisableRateLimitingAttribute => "disabled",
            _ => "-",
        };
    }

    private static string Response(ApiResponseType response)
    {
        var type = response.Type is null || response.Type.Equals(typeof(void)) ? "-" : TypeName(response.Type);
        var media = Join(response.ApiResponseFormats.Select(f => f.MediaType).Order(StringComparer.Ordinal), ",");
        return $"{response.StatusCode} {type} {media}";
    }

    /// <summary>
    ///     The content column, ruling R12: the last content-type-bearing item in endpoint metadata, a
    ///     <see cref="ProducesAttribute"/> or phase 3.4's <c>ProblemContentType</c> record, whichever comes last. MVC lists
    ///     a controller's attributes before its action's, and minimal APIs a group's metadata before its endpoint's, so an
    ///     action- or endpoint-level value overrides the controller's or the group's, as it does on the wire.
    /// </summary>
    /// <remarks>
    ///     The record does not exist on the MVC build, so it is matched by type name and its media type read from its
    ///     <c>ContentType</c> string property. A record with that name and no such property fails the render loudly
    ///     rather than being skipped, so the task that adds it either names the property <c>ContentType</c> or updates
    ///     this hook.
    /// </remarks>
    private static string ContentTypeColumn(IList<object> metadata)
    {
        var item = metadata.LastOrDefault(m =>
            m is ProducesAttribute || string.Equals(m.GetType().Name, ProblemContentTypeName, StringComparison.Ordinal));
        if (item is null)
        {
            return "-";
        }

        if (item is ProducesAttribute produces)
        {
            return Join(produces.ContentTypes, ",");
        }

        var property = item.GetType().GetProperty("ContentType")
            ?? throw new InvalidOperationException(
                $"{item.GetType().FullName} has no ContentType property; update {nameof(ContentTypeColumn)}.");
        return property.GetValue(item) as string
            ?? throw new InvalidOperationException($"{item.GetType().FullName}.ContentType is not a string.");
    }

    private static string TypeName(Type type)
    {
        if (type.IsArray)
        {
            return TypeName(type.GetElementType()!) + "[]";
        }

        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name[..type.Name.IndexOf('`')];
        return $"{name}<{string.Join(',', type.GetGenericArguments().Select(TypeName))}>";
    }

    private static string Join(IEnumerable<string> items, string separator)
    {
        var joined = string.Join(separator, items);
        return joined.Length == 0 ? "-" : joined;
    }

    private static List<string> Lines(string text) =>
        [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>Both sides are sorted, so a multiset difference is the whole diff.</summary>
    private static string Diff(List<string> expected, List<string> actual)
    {
        var onlyExpected = new List<string>(expected);
        var onlyActual = new List<string>();
        foreach (var line in actual)
        {
            if (!onlyExpected.Remove(line))
            {
                onlyActual.Add(line);
            }
        }

        var diff = new StringBuilder();
        foreach (var line in onlyExpected)
        {
            diff.Append("- ").Append(line).Append('\n');
        }

        foreach (var line in onlyActual)
        {
            diff.Append("+ ").Append(line).Append('\n');
        }

        return diff.ToString();
    }
}

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Asp.Versioning;
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
///     <c>RequireAuthorization</c>, <c>AllowAnonymous</c>, <c>RequireRateLimiting</c> and Asp.Versioning's version sets
///     add the same metadata types MVC's attributes are. Nothing is read off a controller type, so a controller and the
///     endpoint group that replaces it render the same line. <see cref="IApiDescriptionGroupCollectionProvider"/> stays
///     registered once MVC is gone, because <c>Program.cs</c> calls <c>AddOpenApi()</c>, which adds the endpoint
///     API explorer.
///     </para>
///     <para>
///     <b>The count.</b> 61 lines, one per controller action. No versioned duplicates: <c>Program.cs</c> adds no
///     versioned API explorer, so each action has one description. The OpenAPI document and Scalar endpoints are
///     excluded from descriptions, the health check is not a described endpoint, and SignalR hubs never are.
///     </para>
///     <para>
///     <b>Versions read <c>-</c> on the MVC build.</b> <c>Program.cs</c> calls <c>AddApiVersioning</c> without
///     <c>.AddMvc()</c>, so Asp.Versioning.Mvc never reads the controllers' <c>[ApiVersion("1.0")]</c>: no action
///     carries <see cref="ApiVersionMetadata"/>, no response carries <c>api-supported-versions</c>, and
///     <c>?api-version=2.0</c> is served like any other request. The column pins that. A port that versions its group
///     changes the line, which is the point: it changes the wire contract too.
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
    ///     this file compiles on both stacks; see <see cref="ProblemContentTypeOf"/>.
    /// </summary>
    private const string ProblemContentTypeName = "ProblemContentType";

    // Red: removing [EnableRateLimiting("write-operations")] from ProjectsController.CreateProject failed with
    //      "- POST api/projects | ... | rate write-operations | ..." and "+ POST api/projects | ... | rate - | ...".
    // Red: changing ProjectsController.DeleteProject's Policy = "ProjectManagement" to "ProjectRead" failed with
    //      "- DELETE api/projects/{id:guid} | auth ProjectManagement,authenticated | ..." and
    //      "+ DELETE api/projects/{id:guid} | auth ProjectRead,authenticated | ...".
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
        Assert.Fail(
            $"The endpoint table differs from Api/{SnapshotFile}. The full table is in {actualPath}.\n"
            + "- is in the snapshot only, + is in the running host only:\n"
            + Diff(Lines(expected), Lines(actual)));
    }

    /// <summary>
    ///     R11's other half: a minimal-API endpoint, mapped the way the port recipe maps one, renders through the same
    ///     <see cref="Render"/> with every column filled from its own metadata, including the <c>ProblemContentType</c>
    ///     hook and Asp.Versioning.Http's version set. Without it, a column that reads blank on minimal APIs would only
    ///     show up as a diff in the first port task, or not at all where MVC's value is blank too.
    /// </summary>
    // Red: dropping .RequireAuthorization("WidgetManagement") rendered "auth authenticated".
    // Red: adding .AllowAnonymous() to the endpoint rendered "anonymous yes".
    // Red: dropping .RequireRateLimiting("write-operations") rendered "rate -".
    // Red: .HasApiVersion(2.0) on the group rendered "versions 2.0". Dropping it stays green: a versioned API's
    //      endpoint with no version of its own takes the default, 1.0, which is what it then serves.
    // Red: new ProblemContentType("application/problem+json") rendered "content application/problem+json".
    [Fact]
    public async Task Render_reads_a_minimal_api_endpoint_from_its_metadata()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorization();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddApiVersioning();
        await using var app = builder.Build();

        app.NewVersionedApi()
            .MapGroup("api/Widgets")
            .HasApiVersion(1.0)
            .RequireAuthorization()
            .WithMetadata(new ProblemContentType("application/json"))
            .MapDelete("{id:guid}", (Guid id) => Results.NoContent())
            .RequireAuthorization("WidgetManagement")
            .RequireRateLimiting("write-operations")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<List<string>>(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // The app's endpoints reach the shared EndpointDataSource when its pipeline is built, at start.
        await app.StartAsync();
        Render(app.Services).Should().Be(
            "DELETE api/widgets/{id:guid} | auth WidgetManagement,authenticated | anonymous no | rate write-operations"
            + " | versions 1.0 | responses 204 - -; 404 ProblemDetails application/problem+json;"
            + " 409 List<String> application/json | content application/json\n");
    }

    /// <summary>Stands in for the endpoint metadata record a phase 3.4 port task adds, matched by its type name.</summary>
    private sealed record ProblemContentType(string ContentType);

    /// <summary>
    ///     Renders the host's endpoint table, one line per <see cref="ApiDescription"/>, sorted ordinally, each ending
    ///     in <c>\n</c>. Fields, separated by <c> | </c>: verb and lower-cased relative path; <c>auth</c>, every
    ///     <see cref="IAuthorizeData"/> as its policy or <c>authenticated</c>, sorted; <c>anonymous</c>; <c>rate</c>, the
    ///     effective <see cref="EnableRateLimitingAttribute"/> policy or <c>-</c>; <c>versions</c>; <c>responses</c>, every
    ///     supported response type as <c>status type media-types</c>, sorted; <c>content</c>, the problem content type or
    ///     MVC's <see cref="ProducesAttribute"/> content types.
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
            "versions " + Versions(metadata),
            "responses " + Join(description.SupportedResponseTypes.Select(Response).Order(StringComparer.Ordinal), "; "),
            "content " + (ProblemContentTypeOf(metadata) ?? ProducesContentType(metadata) ?? "-"),
        };
        return string.Join(" | ", fields);
    }

    /// <summary>
    ///     The relative path, lower-cased, with each route parameter rebuilt from its
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

    /// <summary>
    ///     Asp.Versioning puts one <see cref="ApiVersionMetadata"/> in each versioned endpoint's metadata, from
    ///     <c>[ApiVersion]</c> under Asp.Versioning.Mvc and from an <c>ApiVersionSet</c> under Asp.Versioning.Http.
    /// </summary>
    private static string Versions(IList<object> metadata)
    {
        var versioning = metadata.OfType<ApiVersionMetadata>().ToList();
        if (versioning.Count == 0)
        {
            return "-";
        }

        return Join(versioning.Select(v =>
        {
            if (v.IsApiVersionNeutral)
            {
                return "neutral";
            }

            // Either stack may declare the version explicitly or implicitly, so both mappings are read.
            var declared = v.Map(ApiVersionMapping.Explicit).DeclaredApiVersions
                .Concat(v.Map(ApiVersionMapping.Implicit).DeclaredApiVersions)
                .Distinct()
                .Order()
                .Select(version => version.ToString(null, CultureInfo.InvariantCulture));
            return Join(declared, ",");
        }), " ");
    }

    private static string Response(ApiResponseType response)
    {
        var type = response.Type is null || response.Type.Equals(typeof(void)) ? "-" : TypeName(response.Type);
        var media = Join(response.ApiResponseFormats.Select(f => f.MediaType).Order(StringComparer.Ordinal), ",");
        return $"{response.StatusCode} {type} {media}";
    }

    /// <summary>
    ///     The hook for phase 3.4's <c>ProblemContentType</c> endpoint metadata, which a port task adds. Matched by type
    ///     name because the type does not exist on the MVC build; its media type is read from its <c>ContentType</c>
    ///     string property. A record with that name and no such property fails the render loudly rather than being
    ///     skipped, so the task that adds the record either names the property <c>ContentType</c> or updates this hook.
    /// </summary>
    private static string? ProblemContentTypeOf(IList<object> metadata)
    {
        var item = metadata.LastOrDefault(m => string.Equals(m.GetType().Name, ProblemContentTypeName, StringComparison.Ordinal));
        if (item is null)
        {
            return null;
        }

        var property = item.GetType().GetProperty("ContentType")
            ?? throw new InvalidOperationException(
                $"{item.GetType().FullName} has no ContentType property; update {nameof(ProblemContentTypeOf)}.");
        return property.GetValue(item) as string
            ?? throw new InvalidOperationException($"{item.GetType().FullName}.ContentType is not a string.");
    }

    /// <summary>MVC applies the action's <see cref="ProducesAttribute"/> over the controller's, the last in metadata.</summary>
    private static string? ProducesContentType(IList<object> metadata) =>
        metadata.OfType<ProducesAttribute>().LastOrDefault() is { } produces
            ? Join(produces.ContentTypes, ",")
            : null;

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

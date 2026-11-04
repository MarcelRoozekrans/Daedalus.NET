# Task 6 report: port Agents

Status: DONE. Commit `eb148832 refactor(api): port Agents to minimal API endpoints`, pushed to
`phase/3.4-minimal-apis`. AOT outcome: see the section at the end.

One NEEDS_CONTEXT round came first. A group-root endpoint renders `api/agents/`. The coordinator ruled option 1: the
renderer trims one trailing slash, recorded as R39.

## Endpoint class as written

`src/Daedalus.Api/Endpoints/AgentEndpoints.cs`:

```csharp
public static class AgentEndpoints
{
    public static RouteGroupBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapApiGroup("api/agents")
            .RequireAuthorization("AgentUse")
            .WithTags("Agents")
            .WithMetadata(ProblemContentType.Json);

        group.MapGet(string.Empty, GetAgents);

        return group;
    }

    /// <summary>All available agents, in registration order.</summary>
    public static Ok<IReadOnlyList<AgentSummaryDto>> GetAgents([FromServices] IAgentCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return TypedResults.Ok<IReadOnlyList<AgentSummaryDto>>(catalog.Agents.Select(AgentDtoMapper.ToDto).ToList());
    }
}
```

- The controller had `[Produces("application/json")]`, so the group carries `ProblemContentType.Json`.
- It has no rate limit, as MVC had none.
- RDG output: `GeneratedRouteBuilderExtensions.g.cs` intercepts the `MapGet`. It populates the metadata from
  `Ok<IReadOnlyList<AgentSummaryDto>>` and resolves `catalog` with `GetRequiredService`, `Source = Service`.
- The build shows no `RDG0xx` and no warnings.

## Snapshot line, before and after: identical

```
GET api/agents | auth AgentUse | anonymous no | rate - | versions - | responses 200 IReadOnlyList<AgentSummaryDto> application/json | content application/json
```

`endpoint-contract.snapshot.txt` is untouched. Before R39's trim the port rendered `GET api/agents/`.
`RoutePatternFactory.Combine` always joins the prefix and the pattern with a `/`. A probe confirmed this for
`""`, `"/"`, a leading-slash prefix and nested groups.

OpenAPI: I diffed the full Development document before and after. The `paths` are identical, tag and summary
included. Only the order of the top-level `tags` array changed, with `Agents` now first; the set is the same. That
is recorded in R38.

## Tests, red and green

- `dotnet build -c Release`: 0 warnings, 0 errors.
- `dotnet test tests/Daedalus.Tests.Unit`: 2337 passed.
  - New: `AgentEndpointsTests.GetAgents_maps_the_catalog`. It calls the handler and asserts `result.Value` with
    strict ordering against two agents with distinct descriptions and tools.
  - Red, each applied once in the handler and seen failing:
    1. `TypedResults.Ok<…>([])` failed with "expected 2 items, found empty".
    2. `.Reverse()` failed with Id, Name and Description differing at [0].
    3. A lambda mapper with `""` and `[]` failed on Description and Tools.
- `dotnet test tests/Daedalus.Tests.Integration --filter "FullyQualifiedName~Api|FullyQualifiedName~Authentication"`:
  169 passed, 2 failed.
  - Both failures are `ManufactureSandboxEndToEndTests`, environmental: "Tool source 'roslyn' is unavailable".
  - Green: `EndpointContractSnapshotTests`, both cases; `MvcBehaviourContractTests`; `OpenApiDocumentTests`;
    `EndpointGroupTests`, which now checks a non-empty set; and `ApiAuthorizationTests`, after `AgentsController`
    was removed from its `ControllerTypes`.
  - R39 Red: I deleted the trim in `RoutePath`. Both snapshot tests failed: `Render_reads…` with "api/widgets/",
    and the table test with "+ GET api/agents/ …". Restored. A first attempt, `false &&`, was refused by analyzer
    S1125, so the block was removed instead.
- HTTP coverage of `GET /api/agents` comes from existing tests:
  `AgentEndpointsSmokeTests.Agents_endpoint_requires_authentication`, which checks the 401, and
  `Agents_endpoint_lists_the_configured_agent`, which checks the 200, `application/json` and the Architect entry.
  Both are green on the ported endpoint. No new HTTP case was needed. Neither test carries a `// Red:` line, and I
  did not add one.
- `dotnet test tests/Daedalus.Tests.Playwright.Api`: 115 passed. No Playwright.Api test hits `/api/agents`.

## Files changed

- Added: `src/Daedalus.Api/Endpoints/AgentEndpoints.cs` and
  `tests/Daedalus.Tests.Unit/Endpoints/AgentEndpointsTests.cs`.
- Deleted: `src/Daedalus.Api/Controllers/AgentsController.cs`.
- `src/Daedalus.Api/Endpoints/DaedalusEndpoints.cs`: calls `app.MapAgentEndpoints()`.
- `src/Daedalus.Api/ApiJsonSerializerContext.cs`: a section comment now names `AgentEndpoints`. The types were
  already registered.
- `src/Daedalus.Api/Daedalus.Api.csproj`: the RDG comment no longer says "no endpoint is mapped yet".
- `tests/Daedalus.Tests.Integration/Api/EndpointContractSnapshotTests.cs`: the R39 trim, its doc line, a
  group-root `MapGet` case with its expected line, and a Red line.
- `tests/Daedalus.Tests.Integration/Api/EndpointGroupTests.cs`: the remark no longer says it checks an empty set.
- `tests/Daedalus.Tests.Integration/Authentication/ApiAuthorizationTests.cs`: `AgentsController` is removed from
  the list.
- `docs/plans/2026-10-31-phase-3.4-rulings.md`: R38 and R39.

## R38 and R39

See `docs/plans/2026-10-31-phase-3.4-rulings.md`, entries 38 and 39.

- R38 is the pattern: class, group, tag, `ProblemContentType`, handler naming, the typed result over MVC's declared
  type with the reason it is not `Ok<List<…>>`, `[FromServices]` and the summary. It also records the OpenAPI
  `tags` ordering change.
- R39 is the trailing-slash trim, with the coordinator's reasons and cost.

## Self-review

- The no-contract-change checks pass: status, body, Content-Type, policy, rate limit and the OpenAPI paths.
- No suppressions.
- No reflection fallback; the context is unchanged.
- No route is served by both stacks.
- `dotnet format` left no diff.
- The commit body has no line over 100 characters, no nested parentheses and no trailer.

## Concerns

- `docs/architecture-diagrams.md` still names `AgentsController` in three places. The plan's docs task, plan line
  432, rewrites that file, so I left it.
- The existing HTTP smoke tests have no `// Red:` lines. They still pass on the ported endpoint.
- In the R39 Red, `false &&` hit analyzer S1125, so I deleted the trim block for that run instead. The block was
  restored, and the build is clean.

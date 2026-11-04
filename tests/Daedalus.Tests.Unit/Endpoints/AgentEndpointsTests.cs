using Daedalus.Api.Endpoints;
using Daedalus.Application.DTOs.Agents;
using Microsoft.AspNetCore.Http.HttpResults;
using Thalos;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     <see cref="AgentEndpoints.GetAgents"/> answers what <c>AgentsController.GetAgents</c> answered: 200 with every
///     agent in the catalog, in registration order, mapped by <c>AgentDtoMapper.ToDto</c>.
/// </summary>
public sealed class AgentEndpointsTests
{
    [Fact]
    public void GetAgents_maps_the_catalog()
    {
        var architect = new AgentDefinition
        {
            Id = AgentId.New(),
            Name = "Daedalus Architect",
            Description = "Designs.",
            Instructions = "x",
            Tools = ["search_tasks", "get_project"],
        };
        var scout = new AgentDefinition
        {
            Id = AgentId.New(),
            Name = "scout",
            Description = "Reads.",
            Instructions = "x",
            Tools = ["read_file"],
        };
        var catalog = Substitute.For<IAgentCatalog>();
        catalog.Agents.Returns([architect, scout]);

        Ok<IReadOnlyList<AgentSummaryDto>> result = AgentEndpoints.GetAgents(catalog);

        // Red: TypedResults.Ok<IReadOnlyList<AgentSummaryDto>>([]) in the handler; the catalog's agents are lost.
        // Red: .Reverse() before .ToList() in the handler; registration order is lost.
        // Red: d => new AgentSummaryDto(d.Id.ToString(), d.Name, "", []) in place of AgentDtoMapper.ToDto.
        result.Value.Should().BeEquivalentTo(
            [
                new AgentSummaryDto(architect.Id.ToString(), "Daedalus Architect", "Designs.", ["search_tasks", "get_project"]),
                new AgentSummaryDto(scout.Id.ToString(), "scout", "Reads.", ["read_file"]),
            ],
            options => options.WithStrictOrdering());
    }
}

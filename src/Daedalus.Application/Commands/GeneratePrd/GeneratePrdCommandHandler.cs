using System.Collections.ObjectModel;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Daedalus.Application.Commands.GeneratePrd;

/// <summary>
///     Handler for PRD generation using LLM with PRD agent mode.
/// </summary>
public sealed partial class GeneratePrdCommandHandler(
    IRalphAgentFactory agentFactory,
    ILogger<GeneratePrdCommandHandler> logger) : ICommandHandler<GeneratePrdCommand, Result<PrdResponseDto>>
{
    private const string _prdAgentPrompt = """
                                           You are a Product Requirements Document (PRD) Agent. Your task is to analyze user requirements and generate a structured PRD.

                                           Based on the user's requirements, generate a comprehensive PRD in JSON format with the following structure:
                                           {
                                             "productOverview": "Brief description of what will be built",
                                             "goals": ["goal1", "goal2", ...],
                                             "assumptions": ["assumption1", "assumption2", ...],
                                             "features": [
                                               {
                                                 "title": "Feature name",
                                                 "description": "Detailed description",
                                                 "acceptanceCriteria": "How to verify it works",
                                                 "priority": "High|Medium|Low",
                                                 "estimatedComplexity": "High|Medium|Low",
                                                 "dependencies": ["TASK-001"],
                                                 "phase": "Implementation phase",
                                                 "parallelGroup": 1
                                               }
                                             ],
                                             "tasks": [
                                               {
                                                 "title": "Task name",
                                                 "description": "What needs to be done",
                                                 "acceptanceCriteria": "Success criteria",
                                                 "priority": "High|Medium|Low",
                                                 "estimatedComplexity": "High|Medium|Low",
                                                 "dependencies": [],
                                                 "phase": "Phase name",
                                                 "parallelGroup": 1
                                               }
                                             ]
                                           }

                                           Return ONLY valid JSON, no markdown, no code blocks, no explanations.
                                           """;

    public async Task<Result<PrdResponseDto>> Handle(GeneratePrdCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.UserRequirements))
        {
            return Result.Failure<PrdResponseDto>("User requirements cannot be empty");
        }

        try
        {
            var prompt = BuildPrompt(command);
            LogGeneratingPrd(logger, command.ProjectId);

            // Single invocation path — MCP tools are pre-attached by the agent factory
            var llmResult = await agentFactory.InvokeAsync(prompt, cancellationToken);

            if (llmResult.IsFailure)
            {
                return Result.Failure<PrdResponseDto>($"LLM invocation failed: {llmResult.Error}");
            }

            var prdResult = ParsePrdResponse(llmResult.Value.Response, command.ProjectId, logger);
            if (prdResult.IsFailure)
            {
                return Result.Failure<PrdResponseDto>(prdResult.Error);
            }

            var itemCount = prdResult.Value.AllItems.Count;
            LogPrdGeneratedSuccessfully(logger, itemCount);

            return Result.Success(prdResult.Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating PRD for project {ProjectId}", command.ProjectId);
            return Result.Failure<PrdResponseDto>($"Error generating PRD: {ex.Message}");
        }
    }

    private static string BuildPrompt(GeneratePrdCommand command)
    {
        var contextInfo = !string.IsNullOrWhiteSpace(command.Context)
            ? $"\n\nAdditional Context:\n{command.Context}"
            : string.Empty;

        return $"""
                {_prdAgentPrompt}

                User Requirements (max {command.MaxItems} items):
                {command.UserRequirements}{contextInfo}

                Generate a structured PRD in JSON format.
                """;
    }

    private static Result<PrdResponseDto> ParsePrdResponse(string jsonResponse, Guid projectId, ILogger logger)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonResponse);
            var root = doc.RootElement;

            var productOverview = root.GetProperty("productOverview").GetString() ?? "N/A";
            var goals = root.GetProperty("goals").EnumerateArray()
                .Select(g => g.GetString() ?? string.Empty)
                .ToList();
            var assumptions = root.GetProperty("assumptions").EnumerateArray()
                .Select(a => a.GetString() ?? string.Empty)
                .ToList();

            var features = ParsePrdItems(root.GetProperty("features"));
            var tasks = ParsePrdItems(root.GetProperty("tasks"));

            return Result.Success(new PrdResponseDto(
                Guid.NewGuid().ToString(),
                projectId,
                productOverview,
                goals,
                assumptions,
                features,
                tasks,
                DateTime.UtcNow,
                jsonResponse));
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse PRD JSON response");
            return Result.Failure<PrdResponseDto>("Invalid PRD JSON format from LLM");
        }
    }

    private static List<PrdItemDto> ParsePrdItems(JsonElement itemsElement)
    {
        var items = new List<PrdItemDto>();

        foreach (var item in itemsElement.EnumerateArray())
        {
            var priority = ParsePriority(item, "priority");
            var complexity = ParseComplexity(item, "estimatedComplexity");
            var dependencies = item.TryGetProperty("dependencies", out var depsElement)
                ? new ReadOnlyCollection<string>(
                    depsElement.EnumerateArray().Select(d => d.GetString() ?? string.Empty).ToList())
                : new ReadOnlyCollection<string>([]);

            var prdItem = new PrdItemDto(
                item.GetProperty("title").GetString() ?? string.Empty,
                item.GetProperty("description").GetString() ?? string.Empty,
                item.GetProperty("acceptanceCriteria").GetString() ?? string.Empty,
                priority,
                complexity,
                dependencies.Count,
                dependencies,
                item.TryGetProperty("phase", out var phaseElement)
                    ? phaseElement.GetString() ?? "Implementation"
                    : "Implementation",
                item.TryGetProperty("parallelGroup", out var pgElement)
                    ? pgElement.GetInt32()
                    : 1);

            items.Add(prdItem);
        }

        return items;
    }

    private static Priority ParsePriority(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return Priority.Medium;
        }

        var value = prop.GetString();
        return Enum.TryParse<Priority>(value, true, out var result) ? result : Priority.Medium;
    }

    private static Complexity ParseComplexity(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return Complexity.Medium;
        }

        var value = prop.GetString();
        return Enum.TryParse<Complexity>(value, true, out var result) ? result : Complexity.Medium;
    }

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Generating PRD for project {ProjectId}")]
    private static partial void LogGeneratingPrd(
        ILogger logger,
        Guid projectId);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "PRD generated successfully with {ItemCount} items")]
    private static partial void LogPrdGeneratedSuccessfully(
        ILogger logger,
        int itemCount);
}

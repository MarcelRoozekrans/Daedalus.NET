using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Daedalus.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Services;

/// <summary>
///     Reports whether MCP knowledge base tools are available and provides cached counts.
/// </summary>
public sealed partial class KnowledgeBaseToolStatus(
    McpIntegrationOptions mcpOptions,
    ApplicationDbContext dbContext,
    ILogger<KnowledgeBaseToolStatus> logger) : IKnowledgeBaseToolStatus
{
    private int? _learningsCount;
    private int? _failurePatternsCount;

    public bool AreToolsAvailable =>
        mcpOptions.Enabled &&
        mcpOptions.Servers.ContainsKey("daedalus-knowledge");

    public int LearningsCount
    {
        get
        {
            if (_learningsCount.HasValue)
                return _learningsCount.Value;
            try
            {
                _learningsCount = dbContext.StructuredLearnings.Count();
            }
            catch (Exception ex)
            {
                LogCountFailed(logger, ex, "learnings");
                _learningsCount = 0;
            }

            return _learningsCount.Value;
        }
    }

    public int FailurePatternsCount
    {
        get
        {
            if (_failurePatternsCount.HasValue)
                return _failurePatternsCount.Value;
            try
            {
                _failurePatternsCount = dbContext.TaskExecutions.Count(e => e.Error != null);
            }
            catch (Exception ex)
            {
                LogCountFailed(logger, ex, "failure patterns");
                _failurePatternsCount = 0;
            }

            return _failurePatternsCount.Value;
        }
    }

    [LoggerMessage(EventId = 500, Level = LogLevel.Warning,
        Message = "Failed to count {EntityType} for knowledge base status")]
    private static partial void LogCountFailed(ILogger logger, Exception exception, string entityType);
}

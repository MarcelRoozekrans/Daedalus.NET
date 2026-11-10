namespace Daedalus.Infrastructure.Persistence.Orm.FailurePatterns;

/// <summary>The columns of a failed <c>"TaskExecutions"</c> row the failure pattern search reads.</summary>
internal sealed record ErrorExecutionRow(Guid TaskId, int IterationNumber, string Error, DateTime ExecutedAt);

/// <summary>The columns of a successful <c>"TaskExecutions"</c> row the failure pattern search reads.</summary>
internal sealed record ResolutionExecutionRow(int IterationNumber, string LlmResponse);

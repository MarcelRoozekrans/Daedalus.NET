namespace Daedalus.Api.Services;

/// <summary>The board task a workflow run belongs to.</summary>
public sealed record TaskRunLink(Guid TaskId, string TaskCode, string Title);

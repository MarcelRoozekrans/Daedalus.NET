namespace Daedalus.Domain.CodeAnalysis;

/// <summary>
///     Enumeration for code analysis status
/// </summary>
public enum AnalysisStatus
{
    None = 0,
    Pending = 1,
    ClonePending = 2,
    CloneInProgress = 3,
    Ready = 4,
    AnalysisInProgress = 5,
    AwaitingValidation = 6,
    ValidationFailed = 7,
    Completed = 8,
    Failed = 9,
    Cancelled = 10
}

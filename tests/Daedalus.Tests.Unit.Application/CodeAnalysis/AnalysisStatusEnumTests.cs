using Daedalus.Domain.CodeAnalysis;

namespace Daedalus.Tests.Unit.Application.CodeAnalysis;

/// <summary>
///     Unit tests for analysis status enums
/// </summary>
public class AnalysisStatusEnumTests
{
    [Fact]
    public void AnalysisType_HasAllRequiredValues()
    {
        // Assert
        AnalysisType.Refactor.Should().Be(AnalysisType.Refactor);
        AnalysisType.BugFix.Should().Be(AnalysisType.BugFix);
        AnalysisType.PerformanceOptimization.Should().Be(AnalysisType.PerformanceOptimization);
        AnalysisType.SecurityAudit.Should().Be(AnalysisType.SecurityAudit);
    }

    [Fact]
    public void AnalysisStatus_HasAllRequiredValues()
    {
        // Assert
        AnalysisStatus.Pending.Should().Be(AnalysisStatus.Pending);
        AnalysisStatus.AnalysisInProgress.Should().Be(AnalysisStatus.AnalysisInProgress);
        AnalysisStatus.Completed.Should().Be(AnalysisStatus.Completed);
        AnalysisStatus.Failed.Should().Be(AnalysisStatus.Failed);
    }
}

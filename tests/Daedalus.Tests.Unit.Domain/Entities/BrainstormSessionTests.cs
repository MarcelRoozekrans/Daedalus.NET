using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>
///     Unit tests for BrainstormSession aggregate root.
/// </summary>
public class BrainstormSessionTests
{
    private readonly Guid _projectId = Guid.NewGuid();

    [Fact]
    public void Create_WithValidProjectId_ShouldSucceed()
    {
        var result = BrainstormSession.Create(_projectId);

        result.IsSuccess.Should().BeTrue();
        result.Value.ProjectId.Should().Be(_projectId);
        result.Value.Phase.Should().Be(BrainstormPhase.ContextGathering);
        result.Value.Messages.Should().BeEmpty();
        result.Value.DesignDocument.Should().BeNull();
        result.Value.ImplementationPlan.Should().BeNull();
        result.Value.PhaseCompleteSignaled.Should().BeFalse();
        result.Value.CompletedAt.Should().BeNull();
    }

    [Fact]
    public void Create_WithEmptyProjectId_ShouldFail()
    {
        var result = BrainstormSession.Create(Guid.Empty);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Project");
    }

    [Fact]
    public void AddMessage_WithValidContent_ShouldAppendToMessages()
    {
        var session = BrainstormSession.Create(_projectId).Value;

        var result = session.AddMessage(MessageRole.Assistant, "Welcome to brainstorming!");

        result.IsSuccess.Should().BeTrue();
        session.Messages.Should().HaveCount(1);
        session.Messages[0].Role.Should().Be(MessageRole.Assistant);
        session.Messages[0].Phase.Should().Be(BrainstormPhase.ContextGathering);
    }

    [Fact]
    public void AddMessage_WithPhaseCompleteMarker_ShouldSetFlag()
    {
        var session = BrainstormSession.Create(_projectId).Value;

        var result = session.AddMessage(MessageRole.Assistant, "Context gathered. [PHASE_COMPLETE]");

        result.IsSuccess.Should().BeTrue();
        session.PhaseCompleteSignaled.Should().BeTrue();
        // Marker should be stripped from stored content
        session.Messages[0].Content.Should().NotContain("[PHASE_COMPLETE]");
    }

    [Fact]
    public void AdvancePhase_WhenSignaled_ShouldMoveForward()
    {
        var session = BrainstormSession.Create(_projectId).Value;
        session.AddMessage(MessageRole.Assistant, "Done. [PHASE_COMPLETE]");

        var result = session.AdvancePhase();

        result.IsSuccess.Should().BeTrue();
        session.Phase.Should().Be(BrainstormPhase.Clarification);
        session.PhaseCompleteSignaled.Should().BeFalse();
    }

    [Fact]
    public void AdvancePhase_WhenNotSignaled_ShouldFail()
    {
        var session = BrainstormSession.Create(_projectId).Value;

        var result = session.AdvancePhase();

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not been signaled");
    }

    [Fact]
    public void AdvancePhase_FromTaskCreation_ShouldMoveToCompleted()
    {
        var session = BrainstormSession.Create(_projectId).Value;

        // Advance through all phases: ContextGathering -> Clarification -> Proposals -> DesignReview -> PlanGeneration -> TaskCreation
        for (var i = 0; i < 5; i++)
        {
            session.AddMessage(MessageRole.Assistant, "Done. [PHASE_COMPLETE]");
            session.AdvancePhase();
        }

        session.Phase.Should().Be(BrainstormPhase.TaskCreation);
        session.AddMessage(MessageRole.Assistant, "Tasks ready. [PHASE_COMPLETE]");
        var result = session.AdvancePhase();

        result.IsSuccess.Should().BeTrue();
        session.Phase.Should().Be(BrainstormPhase.Completed);
        session.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void AdvancePhase_WhenCompleted_ShouldFail()
    {
        var session = BrainstormSession.Create(_projectId).Value;
        for (var i = 0; i < 6; i++)
        {
            session.AddMessage(MessageRole.Assistant, "Done. [PHASE_COMPLETE]");
            session.AdvancePhase();
        }

        session.Phase.Should().Be(BrainstormPhase.Completed);
        var result = session.AdvancePhase();

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("terminal");
    }

    [Fact]
    public void Abandon_ShouldSetPhaseToAbandoned()
    {
        var session = BrainstormSession.Create(_projectId).Value;

        var result = session.Abandon();

        result.IsSuccess.Should().BeTrue();
        session.Phase.Should().Be(BrainstormPhase.Abandoned);
        session.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void Abandon_WhenAlreadyCompleted_ShouldFail()
    {
        var session = BrainstormSession.Create(_projectId).Value;
        for (var i = 0; i < 6; i++)
        {
            session.AddMessage(MessageRole.Assistant, "Done. [PHASE_COMPLETE]");
            session.AdvancePhase();
        }

        var result = session.Abandon();

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void SetDesignDocument_ShouldStoreMarkdown()
    {
        var session = BrainstormSession.Create(_projectId).Value;
        var design = "# Architecture\n\nUse clean architecture.";

        var result = session.SetDesignDocument(design);

        result.IsSuccess.Should().BeTrue();
        session.DesignDocument.Should().Be(design);
    }

    [Fact]
    public void SetImplementationPlan_ShouldStoreMarkdown()
    {
        var session = BrainstormSession.Create(_projectId).Value;
        var plan = "## Task 1\n\nCreate the entity.";

        var result = session.SetImplementationPlan(plan);

        result.IsSuccess.Should().BeTrue();
        session.ImplementationPlan.Should().Be(plan);
    }
}

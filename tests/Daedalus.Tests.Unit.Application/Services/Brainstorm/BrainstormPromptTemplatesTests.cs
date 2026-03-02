using Daedalus.Application.Services.Brainstorm;
using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Application.Services.Brainstorm;

public class BrainstormPromptTemplatesTests
{
    [Theory]
    [InlineData(BrainstormPhase.ContextGathering)]
    [InlineData(BrainstormPhase.Clarification)]
    [InlineData(BrainstormPhase.Proposals)]
    [InlineData(BrainstormPhase.DesignReview)]
    [InlineData(BrainstormPhase.PlanGeneration)]
    public void GetSystemPrompt_ForEachPhase_ShouldReturnNonEmptyPrompt(BrainstormPhase phase)
    {
        var prompt = BrainstormPromptTemplates.GetSystemPrompt(phase);
        prompt.Should().NotBeNullOrWhiteSpace();
        prompt.Should().Contain("[PHASE_COMPLETE]");
    }

    [Fact]
    public void GetSystemPrompt_ForContextGathering_ShouldContainProjectPlaceholder()
    {
        var prompt = BrainstormPromptTemplates.GetSystemPrompt(BrainstormPhase.ContextGathering);
        prompt.Should().Contain("{0}"); // project context placeholder
    }

    [Fact]
    public void GetSystemPrompt_ForClarification_ShouldMentionOneQuestion()
    {
        var prompt = BrainstormPromptTemplates.GetSystemPrompt(BrainstormPhase.Clarification);
        prompt.Should().Contain("ONE question");
    }

    [Fact]
    public void GetSystemPrompt_ForProposals_ShouldMention2To3Approaches()
    {
        var prompt = BrainstormPromptTemplates.GetSystemPrompt(BrainstormPhase.Proposals);
        prompt.Should().Contain("2-3");
    }

    [Fact]
    public void GetSystemPrompt_ForPlanGeneration_ShouldMentionTDD()
    {
        var prompt = BrainstormPromptTemplates.GetSystemPrompt(BrainstormPhase.PlanGeneration);
        prompt.Should().Contain("TDD");
    }

    [Theory]
    [InlineData(BrainstormPhase.TaskCreation)]
    [InlineData(BrainstormPhase.Completed)]
    [InlineData(BrainstormPhase.Abandoned)]
    public void GetSystemPrompt_ForTerminalPhases_ShouldReturnEmpty(BrainstormPhase phase)
    {
        var prompt = BrainstormPromptTemplates.GetSystemPrompt(phase);
        prompt.Should().BeEmpty();
    }
}

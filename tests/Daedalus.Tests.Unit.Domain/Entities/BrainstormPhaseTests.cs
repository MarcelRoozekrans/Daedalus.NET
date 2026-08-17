using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>
///     Unit tests for BrainstormPhase enum.
/// </summary>
public class BrainstormPhaseTests
{
    [Fact]
    public void BrainstormPhase_ContextGathering_ShouldHaveValue0()
    {
        // Act & Assert
        ((int)BrainstormPhase.ContextGathering).Should().Be(0);
    }

    [Fact]
    public void BrainstormPhase_Clarification_ShouldHaveValue1()
    {
        // Act & Assert
        ((int)BrainstormPhase.Clarification).Should().Be(1);
    }

    [Fact]
    public void BrainstormPhase_Proposals_ShouldHaveValue2()
    {
        // Act & Assert
        ((int)BrainstormPhase.Proposals).Should().Be(2);
    }

    [Fact]
    public void BrainstormPhase_DesignReview_ShouldHaveValue3()
    {
        // Act & Assert
        ((int)BrainstormPhase.DesignReview).Should().Be(3);
    }

    [Fact]
    public void BrainstormPhase_PlanGeneration_ShouldHaveValue4()
    {
        // Act & Assert
        ((int)BrainstormPhase.PlanGeneration).Should().Be(4);
    }

    [Fact]
    public void BrainstormPhase_TaskCreation_ShouldHaveValue5()
    {
        // Act & Assert
        ((int)BrainstormPhase.TaskCreation).Should().Be(5);
    }

    [Fact]
    public void BrainstormPhase_Completed_ShouldHaveValue6()
    {
        // Act & Assert
        ((int)BrainstormPhase.Completed).Should().Be(6);
    }

    [Fact]
    public void BrainstormPhase_Abandoned_ShouldHaveValue7()
    {
        // Act & Assert
        ((int)BrainstormPhase.Abandoned).Should().Be(7);
    }

    [Fact]
    public void BrainstormPhase_HasEightValues()
    {
        // Act
        var phaseValues = Enum.GetValues<BrainstormPhase>().ToList();

        // Assert
        phaseValues.Should().HaveCount(8);
        phaseValues.Should().Contain(new[]
        {
            BrainstormPhase.ContextGathering,
            BrainstormPhase.Clarification,
            BrainstormPhase.Proposals,
            BrainstormPhase.DesignReview,
            BrainstormPhase.PlanGeneration,
            BrainstormPhase.TaskCreation,
            BrainstormPhase.Completed,
            BrainstormPhase.Abandoned
        });
    }

    [Fact]
    public void BrainstormPhase_AllValuesAreUnique()
    {
        // Arrange
        var phases = Enum.GetValues<BrainstormPhase>();
        var values = phases.Cast<int>().ToList();

        // Act & Assert
        values.Distinct().Should().HaveCount(values.Count);
    }

    [Fact]
    public void BrainstormPhase_CanCastFromInt()
    {
        // Act
        var contextGathering = (BrainstormPhase)0;
        var clarification = (BrainstormPhase)1;
        var proposals = (BrainstormPhase)2;
        var designReview = (BrainstormPhase)3;
        var planGeneration = (BrainstormPhase)4;
        var taskCreation = (BrainstormPhase)5;
        var completed = (BrainstormPhase)6;
        var abandoned = (BrainstormPhase)7;

        // Assert
        contextGathering.Should().Be(BrainstormPhase.ContextGathering);
        clarification.Should().Be(BrainstormPhase.Clarification);
        proposals.Should().Be(BrainstormPhase.Proposals);
        designReview.Should().Be(BrainstormPhase.DesignReview);
        planGeneration.Should().Be(BrainstormPhase.PlanGeneration);
        taskCreation.Should().Be(BrainstormPhase.TaskCreation);
        completed.Should().Be(BrainstormPhase.Completed);
        abandoned.Should().Be(BrainstormPhase.Abandoned);
    }

    [Theory]
    [InlineData(BrainstormPhase.ContextGathering, "ContextGathering")]
    [InlineData(BrainstormPhase.Clarification, "Clarification")]
    [InlineData(BrainstormPhase.Proposals, "Proposals")]
    [InlineData(BrainstormPhase.DesignReview, "DesignReview")]
    [InlineData(BrainstormPhase.PlanGeneration, "PlanGeneration")]
    [InlineData(BrainstormPhase.TaskCreation, "TaskCreation")]
    [InlineData(BrainstormPhase.Completed, "Completed")]
    [InlineData(BrainstormPhase.Abandoned, "Abandoned")]
    public void BrainstormPhase_ToString_ShouldReturnEnumName(BrainstormPhase phase, string expectedName)
    {
        // Act
        var result = phase.ToString();

        // Assert
        result.Should().Be(expectedName);
    }
}

using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>
///     Unit tests for Priority enum.
/// </summary>
public class PriorityEntityTests
{
    [Fact]
    public void Priority_Low_ShouldHaveValue0()
    {
        // Act & Assert
        ((int)Priority.Low).Should().Be(0);
    }

    [Fact]
    public void Priority_Medium_ShouldHaveValue1()
    {
        // Act & Assert
        ((int)Priority.Medium).Should().Be(1);
    }

    [Fact]
    public void Priority_High_ShouldHaveValue2()
    {
        // Act & Assert
        ((int)Priority.High).Should().Be(2);
    }

    [Fact]
    public void Priority_Critical_ShouldHaveValue3()
    {
        // Act & Assert
        ((int)Priority.Critical).Should().Be(3);
    }

    [Fact]
    public void Priority_AllValuesAreUnique()
    {
        // Arrange
        var priorities = new[] { Priority.Low, Priority.Medium, Priority.High, Priority.Critical };
        var values = priorities.Cast<int>().ToList();

        // Act & Assert
        values.Distinct().Should().HaveCount(values.Count);
    }

    [Fact]
    public void Priority_CanCastFromInt()
    {
        // Act
        var low = (Priority)0;
        var medium = (Priority)1;
        var high = (Priority)2;
        var critical = (Priority)3;

        // Assert
        low.Should().Be(Priority.Low);
        medium.Should().Be(Priority.Medium);
        high.Should().Be(Priority.High);
        critical.Should().Be(Priority.Critical);
    }

    [Fact]
    public void Priority_HasFourValues()
    {
        // Act
        var priorityValues = Enum.GetValues<Priority>().ToList();

        // Assert
        priorityValues.Should().HaveCount(4);
        priorityValues.Should().Contain(new[] { Priority.Low, Priority.Medium, Priority.High, Priority.Critical });
    }

    [Theory]
    [InlineData(Priority.Low, "Low")]
    [InlineData(Priority.Medium, "Medium")]
    [InlineData(Priority.High, "High")]
    [InlineData(Priority.Critical, "Critical")]
    public void Priority_ToString_ShouldReturnEnumName(Priority priority, string expectedName)
    {
        // Act
        var result = priority.ToString();

        // Assert
        result.Should().Be(expectedName);
    }

    [Fact]
    public void Priority_CriticalIsHighest()
    {
        // Act & Assert
        ((int)Priority.Critical).Should().BeGreaterThan((int)Priority.High);
        ((int)Priority.Critical).Should().BeGreaterThan((int)Priority.Medium);
        ((int)Priority.Critical).Should().BeGreaterThan((int)Priority.Low);
    }

    [Fact]
    public void Priority_CanCompareValues()
    {
        // Act & Assert
        (Priority.Critical > Priority.High).Should().BeTrue();
        (Priority.High > Priority.Medium).Should().BeTrue();
        (Priority.Medium > Priority.Low).Should().BeTrue();
    }
}

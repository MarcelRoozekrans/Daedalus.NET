using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>
///     Unit tests for Complexity enum.
/// </summary>
public class ComplexityEntityTests
{
    [Fact]
    public void Complexity_Low_ShouldHaveValue0()
    {
        // Act & Assert
        ((int)Complexity.Low).Should().Be(0);
    }

    [Fact]
    public void Complexity_Medium_ShouldHaveValue1()
    {
        // Act & Assert
        ((int)Complexity.Medium).Should().Be(1);
    }

    [Fact]
    public void Complexity_High_ShouldHaveValue2()
    {
        // Act & Assert
        ((int)Complexity.High).Should().Be(2);
    }

    [Fact]
    public void Complexity_AllValuesAreUnique()
    {
        // Arrange
        var complexities = new[] { Complexity.Low, Complexity.Medium, Complexity.High };
        var values = complexities.Cast<int>().ToList();

        // Act & Assert
        values.Distinct().Should().HaveCount(values.Count);
    }

    [Fact]
    public void Complexity_CanCastFromInt()
    {
        // Act
        var low = (Complexity)0;
        var medium = (Complexity)1;
        var high = (Complexity)2;

        // Assert
        low.Should().Be(Complexity.Low);
        medium.Should().Be(Complexity.Medium);
        high.Should().Be(Complexity.High);
    }

    [Fact]
    public void Complexity_HasThreeValues()
    {
        // Act
        var complexityValues = Enum.GetValues<Complexity>().ToList();

        // Assert
        complexityValues.Should().HaveCount(3);
        complexityValues.Should().Contain(new[] { Complexity.Low, Complexity.Medium, Complexity.High });
    }

    [Theory]
    [InlineData(Complexity.Low, "Low")]
    [InlineData(Complexity.Medium, "Medium")]
    [InlineData(Complexity.High, "High")]
    public void Complexity_ToString_ShouldReturnEnumName(Complexity complexity, string expectedName)
    {
        // Act
        var result = complexity.ToString();

        // Assert
        result.Should().Be(expectedName);
    }
}

using Daedalus.Application.DTOs;
using ZeroAlloc.Validation;

namespace Daedalus.Tests.Unit.Application.Validators;

/// <summary>
///     The priority scale is the domain's, Low 0 to Critical 3, and the complexity scale Low 0 to High 2. A value past
///     either end is refused, with a message naming the real ends.
/// </summary>
public sealed class TaskPriorityRangeTests
{
    private static CreateTaskDto Create(int priority, int complexity) =>
        new(Guid.NewGuid(), null, "Title", "Description", priority, null, null, complexity, null, null, null);

    private static UpdateTaskDto Update(int? priority, int? complexity) => new(null, null, priority, null, null, complexity, null);

    /// <summary>Red: leave the create range at 0 to 4; 4 is accepted. Red: keep the old message; the text differs.</summary>
    [Fact]
    public void A_create_with_priority_4_is_refused()
    {
        var result = new CreateTaskDtoValidator().Validate(Create(4, 0));

        result.IsValid.Should().BeFalse();
        Messages(result).Should().Equal("Priority must be between 0 (Low) and 3 (Critical).");
    }

    /// <summary>Red: narrow the create range to 0 to 2; Critical is refused.</summary>
    [Fact]
    public void A_create_with_priority_critical_is_accepted() =>
        new CreateTaskDtoValidator().Validate(Create(3, 2)).IsValid.Should().BeTrue();

    /// <summary>Red: leave the update range at 0 to 4; 4 is accepted. Red: keep the old message; the text differs.</summary>
    [Fact]
    public void An_update_with_priority_4_is_refused()
    {
        var result = new UpdateTaskDtoValidator().Validate(Update(4, null));

        result.IsValid.Should().BeFalse();
        Messages(result).Should().Equal("Priority must be between 0 (Low) and 3 (Critical).");
    }

    /// <summary>Red: leave the create complexity range at 0 to 3; 3 is accepted. Red: keep the old message.</summary>
    [Fact]
    public void A_create_with_complexity_3_is_refused() =>
        Messages(new CreateTaskDtoValidator().Validate(Create(0, 3))).Should()
            .Equal("Estimated complexity must be between 0 (Low) and 2 (High).");

    /// <summary>Red: leave the update complexity range at 0 to 3; 3 is accepted. Red: keep the old message.</summary>
    [Fact]
    public void An_update_with_complexity_3_is_refused() =>
        Messages(new UpdateTaskDtoValidator().Validate(Update(null, 3))).Should()
            .Equal("Estimated complexity must be between 0 (Low) and 2 (High).");

    private static List<string> Messages(ValidationResult result)
    {
        var messages = new List<string>();
        foreach (ref readonly var failure in result.Failures)
        {
            messages.Add(failure.ErrorMessage);
        }

        return messages;
    }
}

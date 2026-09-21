using System.Reflection;
using Daedalus.Application.DTOs;

namespace Daedalus.Tests.Unit.Application.Validators;

/// <summary>
///     <c>Partial_update_touching_only_title_succeeds</c> (in <c>Daedalus.Tests.Integration</c>) proves that a
///     dropped <c>When</c> guard on <see cref="UpdateTaskDto.MaxIterations"/> or
///     <see cref="UpdateTaskDto.ParallelGroup"/> produces an observable 400: both ranges exclude zero, so an
///     omitted value coerced to <c>0</c> fails <c>InclusiveBetween(1, 1000)</c> / <c>GreaterThanOrEqualTo(1)</c>.
///     It cannot prove the same for <see cref="UpdateTaskDto.Priority"/> or
///     <see cref="UpdateTaskDto.EstimatedComplexity"/> — both ranges are <c>InclusiveBetween(0, n)</c>, so a
///     coerced <c>0</c> is a valid value (0 = Critical priority, 0 = Simple complexity) and is
///     indistinguishable, by status code, from a genuine <c>0</c>. Removing either guard changes nothing an HTTP
///     assertion can see.
///     This test closes that structural gap by asserting the wiring directly: each of the four properties'
///     range-validation attribute must carry a <c>When</c> guard naming the matching <c>*HasValue</c> method.
///     Unlike a behavioural test, this is falsifiable regardless of whether the range happens to include zero —
///     if <see cref="UpdateTaskDto.Priority"/>'s range is ever widened to exclude zero (or narrowed to start
///     above it), this test already covers the guard that would then become load-bearing.
/// </summary>
public class UpdateTaskDtoWhenGuardTests
{
    public static TheoryData<string, string> GuardedRangeProperties => new()
    {
        { nameof(UpdateTaskDto.Priority), nameof(UpdateTaskDto.PriorityHasValue) },
        { nameof(UpdateTaskDto.ParallelGroup), nameof(UpdateTaskDto.ParallelGroupHasValue) },
        { nameof(UpdateTaskDto.EstimatedComplexity), nameof(UpdateTaskDto.EstimatedComplexityHasValue) },
        { nameof(UpdateTaskDto.MaxIterations), nameof(UpdateTaskDto.MaxIterationsHasValue) },
    };

    [Theory]
    [MemberData(nameof(GuardedRangeProperties))]
    public void Nullable_range_property_carries_its_HasValue_When_guard(string propertyName, string expectedGuardMethod)
    {
        var property = typeof(UpdateTaskDto).GetProperty(propertyName);
        property.Should().NotBeNull($"UpdateTaskDto must declare a {propertyName} property");

        // Duck-typed rather than pinned to a specific ZeroAlloc.Validation attribute type: Priority and
        // EstimatedComplexity carry InclusiveBetweenAttribute, ParallelGroup carries
        // GreaterThanOrEqualToAttribute, and both expose a public "When" property. Finding "the range attribute
        // with a When property" is stable across which specific range attribute a property happens to use.
        var rangeAttribute = property!.GetCustomAttributes(inherit: false)
            .SingleOrDefault(a => a.GetType().GetProperty("When") is not null);

        rangeAttribute.Should().NotBeNull(
            $"{propertyName} must carry a range-validation attribute with a When guard, otherwise an omitted " +
            "(null) value is coerced to 0 and re-validated against the range");

        var whenValue = rangeAttribute!.GetType().GetProperty("When")!.GetValue(rangeAttribute) as string;

        whenValue.Should().Be(expectedGuardMethod,
            $"{propertyName}'s range attribute must guard with {expectedGuardMethod} so an omitted (null) " +
            "value is not coerced to 0 and re-validated");
    }
}

namespace Daedalus.Tests.Unit.Domain.Abstractions;

/// <summary>
///     Interface for test data builders.
/// </summary>
public interface IBuilder<out T>
{
    T Build();
}

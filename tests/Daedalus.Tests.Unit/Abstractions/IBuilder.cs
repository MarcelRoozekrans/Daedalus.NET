namespace Daedalus.Tests.Unit.Abstractions;

/// <summary>
///     Interface for test data builders.
/// </summary>
public interface IBuilder<out T>
{
    T Build();
}

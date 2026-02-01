namespace Daedalus.Tests.Unit.Abstractions;

/// <summary>
///     Base class for tests that use Spectre.Console TestConsole for capturing and asserting console output.
///     Provides helper methods for common testing scenarios.
/// </summary>
public abstract class SpectreConsoleTestBase
{
    /// <summary>
    ///     Creates a new TestConsole instance for capturing console output.
    /// </summary>
    protected static TestConsole CreateTestConsole() => new();

    /// <summary>
    ///     Creates an interactive TestConsole instance for simulating user input.
    /// </summary>
    protected static TestConsole CreateInteractiveTestConsole()
    {
        var console = new TestConsole();
        console.Interactive();
        return console;
    }

    /// <summary>
    ///     Asserts that console output matches expected content.
    /// </summary>
    protected static void AssertConsoleOutput(TestConsole console, string expectedOutput) =>
        console.Output.Should().Be(expectedOutput);

    /// <summary>
    ///     Asserts that console output contains expected text.
    /// </summary>
    protected static void AssertConsoleOutputContains(TestConsole console, string expectedText) =>
        console.Output.Should().Contain(expectedText);

    /// <summary>
    ///     Asserts that console output starts with expected text.
    /// </summary>
    protected static void AssertConsoleOutputStartsWith(TestConsole console, string expectedText) =>
        console.Output.Should().StartWith(expectedText);

    /// <summary>
    ///     Asserts that console output ends with expected text.
    /// </summary>
    protected static void AssertConsoleOutputEndsWith(TestConsole console, string expectedText) =>
        console.Output.Should().EndWith(expectedText);
}

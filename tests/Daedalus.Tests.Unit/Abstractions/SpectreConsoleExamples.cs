namespace Daedalus.Tests.Unit.Abstractions;

/// <summary>
///     Example tests demonstrating Spectre.Console.Testing capabilities with TestConsole.
///     These examples show how to test console output, widgets, and interactive prompts.
/// </summary>
public class SpectreConsoleExamples : SpectreConsoleTestBase
{
    [Fact]
    public void TestConsole_RenderPanel_CapturesToConsoleOutput()
    {
        // Arrange
        var console = CreateTestConsole();

        // Act
        console.Write(new Panel(new Text("Hello World")));

        // Assert
        AssertConsoleOutputContains(console, "Hello World");
    }

    [Fact]
    public void TestConsole_RenderTable_CapturesTableOutput()
    {
        // Arrange
        var console = CreateTestConsole();
        var table = new Table()
            .AddColumn("Name")
            .AddColumn("Value")
            .AddRow("Item1", "Value1")
            .AddRow("Item2", "Value2");

        // Act
        console.Write(table);

        // Assert
        AssertConsoleOutputContains(console, "Name");
        AssertConsoleOutputContains(console, "Value");
        AssertConsoleOutputContains(console, "Item1");
        AssertConsoleOutputContains(console, "Item2");
    }

    [Fact]
    public void TestConsole_RenderMarkup_CapturesFormattedText()
    {
        // Arrange
        var console = CreateTestConsole();
        var markup = "[bold green]Success![/] Operation completed successfully.";

        // Act
        console.MarkupLine(markup);

        // Assert
        AssertConsoleOutputContains(console, "Success!");
        AssertConsoleOutputContains(console, "Operation completed successfully.");
    }

    [Fact]
    public void TestConsole_TextPrompt_SimulatesUserInput()
    {
        // Arrange
        var console = CreateInteractiveTestConsole();
        console.Input.PushTextWithEnter("Alice");

        // Act
        var name = console.Prompt(new TextPrompt<string>("What's your name?"));

        // Assert
        name.Should().Be("Alice");
        AssertConsoleOutputContains(console, "What's your name?");
    }

    [Fact]
    public void TestConsole_SelectionPrompt_SimulatesChoiceSelection()
    {
        // Arrange
        var console = CreateInteractiveTestConsole();

        // Simulate user selecting second option (down arrow + enter)
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        var fruits = new[] { "Apple", "Banana", "Orange" };
        var prompt = new SelectionPrompt<string>()
            .Title("Choose a fruit:")
            .AddChoices(fruits);

        // Act
        var selected = console.Prompt(prompt);

        // Assert
        selected.Should().Be("Banana");
    }

    [Fact]
    public void TestConsole_MultiSelectionPrompt_SimulatesMultipleChoices()
    {
        // Arrange
        var console = CreateInteractiveTestConsole();

        // Select first item (space), move down (arrow), select second item (space), confirm (enter)
        console.Input.PushKey(ConsoleKey.Spacebar);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Spacebar);
        console.Input.PushKey(ConsoleKey.Enter);

        var items = new[] { "Item1", "Item2", "Item3" };
        var prompt = new MultiSelectionPrompt<string>()
            .Title("Select items:")
            .AddChoices(items);

        // Act
        var selected = console.Prompt(prompt);

        // Assert
        selected.Should().HaveCount(2);
        selected.Should().Contain("Item1");
        selected.Should().Contain("Item2");
    }

    [Fact]
    public void TestConsole_ConfirmPrompt_SimulatesYesResponse()
    {
        // Arrange
        var console = CreateInteractiveTestConsole();
        console.Input.PushTextWithEnter("y");

        var prompt = new ConfirmationPrompt("Do you want to continue?");

        // Act
        var confirmed = console.Prompt(prompt);

        // Assert
        confirmed.Should().BeTrue();
    }

    [Fact]
    public void TestConsole_ProgressDisplay_CapturesProgressOutput()
    {
        // Arrange
        var console = CreateTestConsole();
        var taskCompleted = false;

        // Act - Use AutoRefresh(false) for reliable TestConsole capture
        console.Progress()
            .AutoRefresh(false)
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
            .Start(ctx =>
            {
                var task = ctx.AddTask("[green]Processing[/]", maxValue: 100);
                while (!task.IsFinished)
                {
                    task.Increment(25);
                    ctx.Refresh();
                }

                taskCompleted = true;
            });

        // Assert - Verify the progress ran to completion and produced output
        taskCompleted.Should().BeTrue("Progress task should complete fully");
        console.Output.Should().NotBeNullOrEmpty("Progress should produce console output");
    }

    [Fact]
    public void TestConsole_RuleDisplay_CapturesRuleOutput()
    {
        // Arrange
        var console = CreateTestConsole();
        var rule = new Rule("[bold]Section Title[/]");

        // Act
        console.Write(rule);

        // Assert
        AssertConsoleOutputContains(console, "Section Title");
    }

    [Fact]
    public void TestConsole_MultipleWrites_CapturesAllOutput()
    {
        // Arrange
        var console = CreateTestConsole();

        // Act
        console.WriteLine("Line 1");
        console.WriteLine("[bold]Line 2[/]");
        console.WriteLine("Line 3");

        // Assert
        var output = console.Output;
        output.Should().Contain("Line 1");
        output.Should().Contain("Line 2");
        output.Should().Contain("Line 3");
    }
}

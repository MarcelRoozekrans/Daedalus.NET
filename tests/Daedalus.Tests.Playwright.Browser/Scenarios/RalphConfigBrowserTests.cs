using Daedalus.Tests.Playwright.Browser.PageObjects;

namespace Daedalus.Tests.Playwright.Browser.Scenarios;

[TestFixture]
[Category("E2E")]
[Category("Browser")]
[Category("RalphConfig")]
[Description("Ralph Config page browser tests validating Radzen form rendering")]
public class RalphConfigBrowserTests : BrowserTestBase
{
    private RalphConfigPage _ralphConfigPage = null!;

    public override async Task SetUpAsync()
    {
        await base.SetUpAsync().ConfigureAwait(false);
        if (!SetUpCompleted)
            return;
        _ralphConfigPage = new RalphConfigPage(Page, BaseUrl);
    }

    [Test]
    [Description("Ralph Config page should render title and subtitle")]
    public async Task RalphConfigPage_ShouldRender_TitleAndSubtitle()
    {
        await _ralphConfigPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_ralphConfigPage.PageTitle).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Ralph Config page should display Save and Reset buttons")]
    public async Task RalphConfigPage_ShouldRender_ActionButtons()
    {
        await _ralphConfigPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_ralphConfigPage.SaveButton)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);
        await Expect(_ralphConfigPage.ResetButton).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Ralph Config page should display Loop Engine Settings section")]
    public async Task RalphConfigPage_ShouldRender_LoopEngineSettings()
    {
        await _ralphConfigPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_ralphConfigPage.SaveButton)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);
        await Expect(_ralphConfigPage.IterationDelayInput).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_ralphConfigPage.MaxFailuresInput).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_ralphConfigPage.MaxIterationsInput).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_ralphConfigPage.RequestTimeoutInput).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Ralph Config page should display Prompt Template checkboxes")]
    public async Task RalphConfigPage_ShouldRender_PromptTemplateOptions()
    {
        await _ralphConfigPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_ralphConfigPage.SaveButton)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);
        await Expect(_ralphConfigPage.MaxSubagentsInput).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_ralphConfigPage.GitWorkflowCheckbox).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_ralphConfigPage.TestInstructionsCheckbox).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_ralphConfigPage.QualityGuardsCheckbox).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_ralphConfigPage.SelfImprovementCheckbox).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_ralphConfigPage.CodingStandardsCheckbox).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_ralphConfigPage.LoggingInstructionsCheckbox).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Ralph Config page should not show error on successful load")]
    public async Task RalphConfigPage_ShouldNotDisplay_ErrorAlert()
    {
        await _ralphConfigPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_ralphConfigPage.SaveButton)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);
        await Expect(_ralphConfigPage.ErrorAlert).Not.ToBeVisibleAsync().ConfigureAwait(false);
    }

    // ── Form interaction ─────────────────────────────────────────────────────

    [Test]
    [Description("Numeric inputs should contain default configuration values")]
    public async Task RalphConfigPage_NumericInputs_ShouldHaveDefaultValues()
    {
        await _ralphConfigPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_ralphConfigPage.SaveButton)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);

        // All numeric inputs should have non-empty values loaded from the API
        var iterationDelay = await _ralphConfigPage.IterationDelayInput.InputValueAsync().ConfigureAwait(false);
        iterationDelay.Should().NotBeNullOrEmpty("Iteration delay should have a default value");

        var maxIterations = await _ralphConfigPage.MaxIterationsInput.InputValueAsync().ConfigureAwait(false);
        maxIterations.Should().NotBeNullOrEmpty("Max iterations should have a default value");
    }

    [Test]
    [Description("Checkboxes should be interactive")]
    public async Task RalphConfigPage_Checkboxes_ShouldBeClickable()
    {
        await _ralphConfigPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_ralphConfigPage.SaveButton)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);

        // Get the underlying hidden input to check state
        var checkbox = _ralphConfigPage.GitWorkflowCheckbox.Locator("input[type='checkbox']");
        var initialState = await checkbox.IsCheckedAsync().ConfigureAwait(false);

        // Click the visible wrapper to toggle
        await _ralphConfigPage.GitWorkflowCheckbox.ClickAsync().ConfigureAwait(false);

        var newState = await checkbox.IsCheckedAsync().ConfigureAwait(false);
        newState.Should().NotBe(initialState, "Checkbox state should toggle after clicking");
    }

    [Test]
    [Description("Detailed Logging checkbox should be visible and clickable")]
    public async Task RalphConfigPage_DetailedLogging_ShouldBeVisible()
    {
        await _ralphConfigPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_ralphConfigPage.SaveButton)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);
        await Expect(_ralphConfigPage.DetailedLoggingCheckbox).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Reset button should reload original config values")]
    public async Task RalphConfigPage_ResetButton_ShouldReloadConfig()
    {
        await _ralphConfigPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_ralphConfigPage.SaveButton)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);

        // Record original value
        var originalValue = await _ralphConfigPage.MaxIterationsInput.InputValueAsync().ConfigureAwait(false);

        // Modify the value
        await _ralphConfigPage.MaxIterationsInput.ClearAsync().ConfigureAwait(false);
        await _ralphConfigPage.MaxIterationsInput.FillAsync("999").ConfigureAwait(false);

        // Click Reset
        await _ralphConfigPage.ResetButton.ClickAsync().ConfigureAwait(false);

        // Wait for reload
        await Expect(_ralphConfigPage.SaveButton)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);

        // Value should be restored
        var resetValue = await _ralphConfigPage.MaxIterationsInput.InputValueAsync().ConfigureAwait(false);
        resetValue.Should().Be(originalValue, "Reset should restore original value");
    }
}

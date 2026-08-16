using Daedalus.Tests.Playwright.Browser.PageObjects;

namespace Daedalus.Tests.Playwright.Browser.Scenarios;

[TestFixture]
[Category("E2E")]
[Category("Browser")]
[Category("PrdGenerator")]
[Description("PRD Generator browser tests validating multi-step wizard rendering and navigation")]
public class PrdGeneratorBrowserTests : BrowserTestBase
{
    private PrdGeneratorPage _prdPage = null!;

    public override async Task SetUpAsync()
    {
        await base.SetUpAsync().ConfigureAwait(false);
        if (!SetUpCompleted)
            return;
        _prdPage = new PrdGeneratorPage(Page, BaseUrl);
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    [Test]
    [Description("PRD Generator page should render title and subtitle")]
    public async Task PrdGenerator_ShouldRender_TitleAndSubtitle()
    {
        await _prdPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_prdPage.PageTitle).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_prdPage.Subtitle).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("PRD Generator should display the steps indicator")]
    public async Task PrdGenerator_ShouldRender_StepsIndicator()
    {
        await _prdPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_prdPage.Steps).ToBeVisibleAsync().ConfigureAwait(false);

        var stepCount = await _prdPage.StepItems.CountAsync().ConfigureAwait(false);
        stepCount.Should().Be(4, "PRD wizard has 4 steps");
    }

    [Test]
    [Description("Steps indicator should show all four step labels")]
    public async Task PrdGenerator_Steps_ShouldDisplayAllLabels()
    {
        await _prdPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_prdPage.Steps).ToBeVisibleAsync().ConfigureAwait(false);

        await Expect(_prdPage.Steps.GetByText("Select Project")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_prdPage.Steps.GetByText("Requirements")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_prdPage.Steps.GetByText("Review PRD")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_prdPage.Steps.GetByText("Tasks Created")).ToBeVisibleAsync().ConfigureAwait(false);
    }

    // ── Step 1: Project Selection ────────────────────────────────────────────

    [Test]
    [Description("Step 1 should display the project selection heading")]
    public async Task PrdGenerator_Step1_ShouldDisplayProjectSelection()
    {
        await _prdPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_prdPage.SelectProjectHeading).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);
    }

    [Test]
    [Description("Step 1 should show project cards for seeded projects")]
    public async Task PrdGenerator_Step1_ShouldShowProjectCards()
    {
        await _prdPage.NavigateAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);

        // The E2E fixture seeds a "Test Project"
        await Expect(_prdPage.GetProjectCardByName("Test Project")).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);
    }

    [Test]
    [Description("Clicking a project card should advance to step 2 (Requirements)")]
    public async Task PrdGenerator_Step1_ClickProject_ShouldAdvanceToStep2()
    {
        await _prdPage.NavigateAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);

        await Expect(_prdPage.GetProjectCardByName("Test Project")).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);

        await _prdPage.GetProjectCardByName("Test Project").ClickAsync().ConfigureAwait(false);

        // Step 2 should now be visible with a Generate PRD button or requirements textarea
        await Expect(_prdPage.GenerateButton.Or(_prdPage.CancelButton)).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);
    }

    // ── Step 2: Requirements Input ───────────────────────────────────────────

    [Test]
    [Description("Step 2 should have a Cancel button to return to project selection")]
    public async Task PrdGenerator_Step2_CancelButton_ShouldResetToStep1()
    {
        await _prdPage.NavigateAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);

        // Advance to step 2
        await Expect(_prdPage.GetProjectCardByName("Test Project")).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);
        await _prdPage.GetProjectCardByName("Test Project").ClickAsync().ConfigureAwait(false);

        await Expect(_prdPage.CancelButton).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);
        await _prdPage.CancelButton.ClickAsync().ConfigureAwait(false);

        // Should return to step 1
        await Expect(_prdPage.SelectProjectHeading).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);
    }

    // ── No error on load ─────────────────────────────────────────────────────

    [Test]
    [Description("PRD Generator should not show error alerts on initial load")]
    public async Task PrdGenerator_ShouldNotDisplay_ErrorAlert()
    {
        await _prdPage.NavigateAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);

        await Expect(_prdPage.ErrorAlert).Not.ToBeVisibleAsync().ConfigureAwait(false);
    }
}

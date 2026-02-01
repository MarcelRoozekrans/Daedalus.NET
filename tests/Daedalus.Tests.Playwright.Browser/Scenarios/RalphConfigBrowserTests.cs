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
        if (!SetUpCompleted) return;
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
}

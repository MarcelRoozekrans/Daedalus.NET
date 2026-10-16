using System.Text.RegularExpressions;
using AwesomeAssertions.Execution;
using Daedalus.Tests.Playwright.Browser.Fixtures;
using Daedalus.Tests.Playwright.Browser.Utilities;

namespace Daedalus.Tests.Playwright.Browser.Scenarios;

/// <summary>
///     Phase 2.14, R7: pins what the Brainstorm page does today, before it moves onto Flux. Each scenario starts its own
///     session through the real repository (<see cref="BrainstormSeeds"/>), and the real API answers with the stubbed model.
/// </summary>
[TestFixture]
[Category("E2E")]
[Category("Browser")]
[Category("Brainstorm")]
[Description("Brainstorm: open a session, send a message, advance a phase")]
public class BrainstormPageBrowserTests : BrowserTestBase
{
    private const string UserMessage = "Small shop owners, who need stock counts first.";

    private ILocator Phase(string label) =>
        Page.Locator(".rz-badge", new PageLocatorOptions { HasTextRegex = new Regex($"^{label}$") });

    private ILocator Message(string speaker, string text) =>
        Page.Locator(".rz-card", new PageLocatorOptions { HasText = text })
            .Filter(new LocatorFilterOptions { Has = Page.GetByText(speaker, new PageGetByTextOptions { Exact = true }) });

    private ILocator AdvanceButton => Page.GetByTestId("btn-advance-phase");

    private async Task OpenNewSessionAsync()
    {
        var sessionId = await E2EServerFixture.Brainstorms.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await Page.GotoAsync(new Uri(BaseUrl, $"/brainstorm/{sessionId}").ToString()).ConfigureAwait(false);
        await Expect(Message("Assistant", BrainstormSeeds.OpeningMessage)).ToBeVisibleAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     With a session open the page is exactly as tall as the content column, at desktop, tablet and phone widths:
    ///     the composer and Send sit above the footer, the body has nothing to scroll, and the page reaches the column's
    ///     bottom, so the composer sits at its foot.
    ///     Red: the page's stack back on <c>height: calc(100vh - 80px)</c> without <c>page-fill</c>; the composer's and Send's
    ///     bottoms are 38px under the footer's top, the body scrolls 78px and the page ends 78px past the column, at
    ///     1920x1080 and 768x1024; 52px and 92px at 375x812. All four assertions go red.
    ///     Red: the theme's <c>.rz-body > div:has(> .page-fill)</c> rule removed; the wrapper is only as tall as its
    ///     content, the page's 100% resolves to auto, and the page ends hundreds of pixels above the column's bottom.
    ///     Only the gap assertion goes red: a short transcript still fits.
    /// </summary>
    [TestCase(1920, 1080)]
    [TestCase(768, 1024)]
    [TestCase(375, 812)]
    [Description("With a session open the Brainstorm page fits above the footer")]
    public async Task With_a_session_open_the_brainstorm_page_fits_above_the_footer(int width, int height)
    {
        await Page.SetViewportSizeAsync(width, height).ConfigureAwait(false);
        await OpenNewSessionAsync().ConfigureAwait(false);
        var composer = Page.GetByTestId("brainstorm-input");
        await Expect(composer).ToBeVisibleAsync().ConfigureAwait(false);

        var composerOverlap = await LayoutFit.OverlapWithFooterAsync(Page, composer).ConfigureAwait(false);
        var sendOverlap = await LayoutFit.OverlapWithFooterAsync(Page, Page.GetByTestId("btn-send-message")).ConfigureAwait(false);
        var bodyOverflow = await LayoutFit.BodyOverflowAsync(Page).ConfigureAwait(false);
        var gapBelow = await LayoutFit.GapBelowAsync(Page, Page.Locator(".rz-body > div > *").First).ConfigureAwait(false);

        // All four are reported together: each Red line turns its own assertion red.
        using (new AssertionScope())
        {
            composerOverlap.Should().BeLessThanOrEqualTo(0, "the composer's bottom is above the footer's top");
            sendOverlap.Should().BeLessThanOrEqualTo(0, "Send's bottom is above the footer's top");
            bodyOverflow.Should().BeLessThanOrEqualTo(0, "the page fits the column, so the body does not scroll");
            gapBelow.Should().BeInRange(-1, 1, "the page fills the column down to its bottom, so the composer sits at the foot");
        }
    }

    /// <summary>
    ///     A session opens on its messages, its current phase marked, and Advance offered when the phase is complete.
    ///     Red: the message card renders <c>message.Phase</c> for its content; the opening message does not show.
    ///     Red: <c>isCurrent</c> compares against <c>BrainstormPhase.Clarification</c>; Context is not the primary badge.
    ///     Red: <c>GetPhaseBadgeStyle</c> returns <c>BadgeStyle.Secondary</c> for a phase not reached; Clarify is not light.
    ///     Red: the Advance bar shows only when <c>PhaseCompleteSignaled</c> is false; no Advance button shows.
    ///     Red: <c>GetNextPhaseName</c> maps ContextGathering to "Proposals"; the button does not read Clarification.
    /// </summary>
    [Test]
    [Description("A session opens on its messages and phase, with Advance offered")]
    public async Task A_session_opens_on_its_messages_and_phase_with_advance_offered()
    {
        await OpenNewSessionAsync().ConfigureAwait(false);

        await Expect(Phase("Context")).ToHaveClassAsync(new Regex(@"\brz-badge-primary\b")).ConfigureAwait(false);
        await Expect(Phase("Clarify")).ToHaveClassAsync(new Regex(@"\brz-badge-light\b")).ConfigureAwait(false);
        await Expect(AdvanceButton).ToHaveTextAsync(new Regex("Advance to Clarification")).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sending a message posts it, reloads the session and shows it as the person's with the model's reply, and clears
    ///     the input. The API keeps the message, so the reload shows it.
    ///     Red: <c>SendMessage</c> passes <c>_userInput</c> to <c>BrainstormEffects.SendAsync</c> after clearing it; the API
    ///     refuses the empty message and nothing new shows.
    ///     Red: <c>SendMessage</c> does not call <c>LoadSession</c> after the send; the sent message never shows.
    ///     Red: the person's caption reads "Me"; the sent message is not captioned You.
    ///     Red: the message loop skips assistant messages after the first; the model's reply does not show.
    ///     Red: remove <c>ValueGeneratedNever</c> from <c>BrainstormMessageConfiguration</c>; the API does not keep the message.
    ///     Red: <c>_userInput</c> is not cleared; the input still holds the message.
    /// </summary>
    [Test]
    [Description("Sending a message shows it and the reply, and clears the input")]
    public async Task Sending_a_message_shows_it_and_the_reply_and_clears_the_input()
    {
        await OpenNewSessionAsync().ConfigureAwait(false);

        var input = Page.GetByTestId("brainstorm-input");
        await input.FillAsync(UserMessage).ConfigureAwait(false);
        await input.PressAsync("Tab").ConfigureAwait(false);
        await Page.GetByTestId("btn-send-message").ClickAsync().ConfigureAwait(false);

        await Expect(Message("You", UserMessage)).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(Message("Assistant", BrainstormSeeds.StubReply)).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(input).ToHaveValueAsync(string.Empty).ConfigureAwait(false);
    }

    /// <summary>
    ///     Advance moves the session to the next phase, marks the last one done, shows the model's opening of the new
    ///     phase, and withdraws the button until the new phase is complete.
    ///     Red: <c>BrainstormEffects.AdvanceAsync</c> does not dispatch the returned session; Clarify never becomes the
    ///     primary badge.
    ///     Red: <c>isCompleted</c> compares with <c>&lt;</c>; Context is not marked done.
    ///     Red: <c>BrainstormEffects.AdvanceAsync</c> dispatches the returned session with the held messages; the model's
    ///     reply does not show.
    ///     Red: the Advance bar shows for any loaded session; the button stays.
    /// </summary>
    [Test]
    [Description("Advance moves the session to the next phase")]
    public async Task Advance_moves_the_session_to_the_next_phase()
    {
        await OpenNewSessionAsync().ConfigureAwait(false);

        await AdvanceButton.ClickAsync().ConfigureAwait(false);

        await Expect(Phase("Clarify")).ToHaveClassAsync(new Regex(@"\brz-badge-primary\b")).ConfigureAwait(false);
        await Expect(Phase("Context")).ToHaveClassAsync(new Regex(@"\brz-badge-success\b")).ConfigureAwait(false);
        await Expect(Message("Assistant", BrainstormSeeds.StubReply)).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(AdvanceButton).ToHaveCountAsync(0).ConfigureAwait(false);
    }
}

using System.Text.RegularExpressions;
using Daedalus.Application.DTOs;
using Daedalus.Tests.Playwright.Browser.Fixtures;
using BrainstormPhase = Daedalus.Domain.Entities.BrainstormPhase;

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
    ///     Sending a message posts it, reloads the session and shows it as the person's, and clears the input. The session
    ///     is answered here: on this host the API does not keep a sent message, so a reload from it would not hold one
    ///     (see the task 1 report). Once a message is posted, the answer is the session the API returned with the message
    ///     and the reply added.
    ///     Red: <c>SendMessage</c> posts <c>_userInput</c> after clearing it; the posted content is empty.
    ///     Red: <c>SendMessage</c> does not reload the session; the sent message never shows.
    ///     Red: the person's caption reads "Me"; the sent message is not captioned You.
    ///     Red: <c>_userInput</c> is not cleared; the input still holds the message.
    /// </summary>
    [Test]
    [Description("Sending a message shows it and clears the input")]
    public async Task Sending_a_message_shows_it_and_clears_the_input()
    {
        var sessionId = await E2EServerFixture.Brainstorms.StartAsync(CancellationToken.None).ConfigureAwait(false);
        var posted = new List<string>();
        await Page.RouteAsync($"**/api/brainstorm/sessions/{sessionId}**", async route =>
        {
            if (string.Equals(route.Request.Method, "POST", StringComparison.Ordinal))
            {
                posted.Add(JsonDocument.Parse(route.Request.PostData!).RootElement.GetProperty("content").GetString()!);
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "application/json",
                    Body = JsonSerializer.Serialize(Dto("Assistant", BrainstormSeeds.StubReply), JsonSerializerOptions.Web),
                }).ConfigureAwait(false);
                return;
            }

            var fetched = await route.FetchAsync().ConfigureAwait(false);
            var session = System.Text.Json.Nodes.JsonNode.Parse(await fetched.TextAsync().ConfigureAwait(false))!;
            if (posted.Count > 0)
            {
                var messages = session["messages"]!.AsArray();
                messages.Add(JsonSerializer.SerializeToNode(Dto("User", UserMessage), JsonSerializerOptions.Web));
                messages.Add(JsonSerializer.SerializeToNode(Dto("Assistant", BrainstormSeeds.StubReply), JsonSerializerOptions.Web));
            }

            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = session.ToJsonString(),
            }).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await Page.GotoAsync(new Uri(BaseUrl, $"/brainstorm/{sessionId}").ToString()).ConfigureAwait(false);
        await Expect(Message("Assistant", BrainstormSeeds.OpeningMessage)).ToBeVisibleAsync().ConfigureAwait(false);

        var input = Page.GetByTestId("brainstorm-input");
        await input.FillAsync(UserMessage).ConfigureAwait(false);
        await input.PressAsync("Tab").ConfigureAwait(false);
        await Page.GetByTestId("btn-send-message").ClickAsync().ConfigureAwait(false);

        await Expect(Message("You", UserMessage)).ToBeVisibleAsync().ConfigureAwait(false);
        posted.Should().Equal(UserMessage);
        await Expect(input).ToHaveValueAsync(string.Empty).ConfigureAwait(false);
    }

    private static BrainstormMessageDto Dto(string role, string content) =>
        new(Guid.NewGuid(), role, content, BrainstormPhase.ContextGathering, DateTime.UtcNow);

    /// <summary>
    ///     Advance moves the session to the next phase, marks the last one done, shows the model's opening of the new
    ///     phase, and withdraws the button until the new phase is complete.
    ///     Red: <c>AdvancePhase</c> does not take the returned session; Clarify never becomes the primary badge.
    ///     Red: <c>isCompleted</c> compares with <c>&lt;</c>; Context is not marked done.
    ///     Red: <c>AdvancePhase</c> keeps the old messages on the returned session; the model's reply does not show.
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

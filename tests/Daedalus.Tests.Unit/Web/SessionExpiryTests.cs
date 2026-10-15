using System.Text.Json;
using Daedalus.Web.Auth;
using Daedalus.Web.Services;
using Microsoft.Extensions.Time.Testing;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     A request whose token cannot be refreshed ends the session: the stored OIDC user is cleared, the browser goes to
///     sign-in returning to the same page, and the caller gets a failure, never "Please log in" and never an exception.
/// </summary>
public sealed class SessionExpiryTests : IDisposable
{
    private readonly ExpiredSession _session = new();

    public void Dispose() => _session.Dispose();

    /// <summary>Every client call that reaches the API, by name, with the failure it returns.</summary>
    public static TheoryData<string> Calls() => new()
    {
        "ApiClient.GetTasksAsync",
        "ApiClient.CreateTaskAsync",
        "ApiClient.DeleteTaskAsync",
        "ApiClient.GetRunAsync",
        "AgentApiClient.GetAgentsAsync",
        "AgentApiClient.CreateSessionAsync",
        "AgentApiClient.CloseSessionAsync",
        "AgentApiClient.ForgetMemoryAsync",
        "ProjectApiClient.CreateProjectAsync",
        "ProjectApiClient.UpdateProjectAsync",
        "ProjectApiClient.DeleteProjectAsync",
    };

    /// <summary>
    ///     Each client call whose token cannot be refreshed returns the signing-in failure, and the session has ended:
    ///     the stored user is cleared and the browser goes to sign-in, returning to the page it was on.
    ///     Red: restore "Please log in to access this data." in ApiClient.GetAsync; GetTasksAsync's error differs.
    ///     Red: drop the AccessTokenNotAvailableException catches from ProjectApiClient; its errors read "Error ...".
    ///     Red: make SessionExpiryHandler's catch skip SignInAgainAsync; no clear and no navigation is recorded.
    ///     Red: skip ClearStoredUserAsync in SignInAgainAsync; no clearStoredUser call is recorded.
    ///     Red: NavigateToLogin "authentication/log-in" in SignInAgainAsync; the navigation's URI differs.
    ///     Red: call NavigateTo, not the exception's Redirect, in SignInAgainAsync; the return URL is null.
    ///     Red: drop <c>Expired = true</c> from SignInAgainAsync; Expired is false.
    /// </summary>
    [Theory]
    [MemberData(nameof(Calls))]
    public async Task A_request_whose_token_cannot_be_refreshed_signs_in_again(string call)
    {
        var error = await CallAsync(call);

        error.Should().Be(SessionExpiry.SigningInAgain);
        _session.Js.Calls.Should().Equal("import ./js/session.js", "module.clearStoredUser");
        var navigation = _session.Navigation.Navigations.Should().ContainSingle().Subject;
        navigation.Uri.Should().Be("authentication/login");
        ReturnUrl(navigation.State).Should().Be(RecordingNavigation.Page);
        _session.Expiry.Expired.Should().BeTrue();
    }

    /// <summary>
    ///     A second failure while the first sign-in is under way does not navigate again, so the history holds one
    ///     sign-in entry.
    ///     Red: drop the Expired check at the top of SignInAgainAsync; two navigations are recorded.
    /// </summary>
    [Fact]
    public async Task A_second_failure_does_not_redirect_again()
    {
        await CallAsync("ApiClient.GetTasksAsync");
        await CallAsync("ApiClient.CreateTaskAsync");

        _session.Navigation.Navigations.Should().ContainSingle();
    }

    /// <summary>
    ///     After a new authentication state, a session that ends again goes to sign-in again.
    ///     Red: make Reset leave Expired set; the second failure records no navigation.
    /// </summary>
    [Fact]
    public async Task A_failure_after_a_new_sign_in_redirects_again()
    {
        await CallAsync("ApiClient.GetTasksAsync");
        _session.Expiry.Reset();

        await CallAsync("ApiClient.GetTasksAsync");

        _session.Expiry.Expired.Should().BeTrue();
        _session.Navigation.Navigations.Should().HaveCount(2);
    }

    /// <summary>
    ///     The browser leaves for sign-in only once the layout's notice has been on screen for 1.5 seconds, so it can be
    ///     read; the session has ended, and its stored user is gone, from the start.
    ///     Red: the wait dropped from SignInAgainAsync; the navigation is recorded before any time passes.
    ///     Red: <c>NoticeTime</c> set to 100ms, as the 0.1s the review measured; the navigation is recorded 1.499s in.
    ///     Red: the wait placed before ClearStoredUserAsync; no clearStoredUser call is recorded before time passes.
    ///     Red: <c>Expired = true</c> and its event moved after the wait; Expired is false before time passes.
    ///     Red: the Redirect dropped after the wait; no navigation is recorded once 1.5 seconds have passed.
    ///     Red: restore "Please log in to access this data." in ApiClient.GetAsync; the call's error differs.
    /// </summary>
    [Fact]
    public async Task The_redirect_waits_while_the_notice_is_read()
    {
        var time = new FakeTimeProvider();
        using var session = new ExpiredSession(time);

        var call = CallAsync(session, "ApiClient.GetTasksAsync");

        session.Expiry.Expired.Should().BeTrue("the session ends at once");
        session.Js.Calls.Should().Contain("module.clearStoredUser", "the stored user is cleared at once");
        session.Navigation.Navigations.Should().BeEmpty("the notice is on screen first");
        time.Advance(TimeSpan.FromMilliseconds(1499));
        session.Navigation.Navigations.Should().BeEmpty("the notice stays for 1.5 seconds");
        time.Advance(TimeSpan.FromMilliseconds(1));
        (await call).Should().Be(SessionExpiry.SigningInAgain);
        session.Navigation.Navigations.Should().ContainSingle("then the browser goes to sign in");
    }

    private Task<string?> CallAsync(string call) => CallAsync(_session, call);

    private static async Task<string?> CallAsync(ExpiredSession session, string call)
    {
        var http = session.Http;
        var api = new ApiClient(http);
        var agents = new AgentApiClient(http);
        var projects = new ProjectApiClient(api, http);
        return call switch
        {
            "ApiClient.GetTasksAsync" => (await api.GetTasksAsync()).Error,
            "ApiClient.CreateTaskAsync" => (await api.CreateTaskAsync(new CreateTaskDto(
                Guid.Empty, null, "t", "d", 1, null, 0, 0, "p", null, null))).Error,
            "ApiClient.DeleteTaskAsync" => (await api.DeleteTaskAsync(Guid.NewGuid())).Error,
            "ApiClient.GetRunAsync" => (await api.GetRunAsync(Guid.NewGuid())).Error,
            "AgentApiClient.GetAgentsAsync" => (await agents.GetAgentsAsync()).Error,
            "AgentApiClient.CreateSessionAsync" => (await agents.CreateSessionAsync("agent")).Error,
            "AgentApiClient.CloseSessionAsync" => (await agents.CloseSessionAsync("session")).Error,
            "AgentApiClient.ForgetMemoryAsync" => (await agents.ForgetMemoryAsync("memory")).Error,
            "ProjectApiClient.CreateProjectAsync" => (await projects.CreateProjectAsync(new CreateProjectDto("a", "d", "1"))).Error,
            "ProjectApiClient.UpdateProjectAsync" => (await projects.UpdateProjectAsync(Guid.NewGuid(), new UpdateProjectDto("a", "d", "1"))).Error,
            "ProjectApiClient.DeleteProjectAsync" => (await projects.DeleteProjectAsync(Guid.NewGuid())).Error,
            _ => throw new ArgumentOutOfRangeException(nameof(call), call, "Not a client call."),
        };
    }

    /// <summary>The return URL in a sign-in navigation's history state, as Blazor's NavigateToLogin writes it.</summary>
    private static string? ReturnUrl(string? state)
    {
        if (state is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(state);
        return document.RootElement.EnumerateObject()
            .Where(p => string.Equals(p.Name, "returnUrl", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Value.GetString())
            .FirstOrDefault();
    }
}

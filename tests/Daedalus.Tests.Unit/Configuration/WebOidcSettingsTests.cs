using System.Text.Json;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     The console's OIDC settings send the browser back through Blazor's callbacks. After Log out the identity provider
///     returns to <c>authentication/logout-callback</c>, which Blazor handles and then leaves for the return URL Log out
///     gave, Home. Returning to <c>/</c> itself, as before, left the provider's <c>?state=</c> on the landing's address.
///     The browser tests run in test mode, with no identity provider, so the settings are read here.
/// </summary>
public sealed class WebOidcSettingsTests
{
    /// <summary>
    ///     Both of the Web project's settings files, the served one and the project one, name the callbacks.
    ///     Red: PostLogoutRedirectUri back to <c>http://localhost:5290/</c> in wwwroot/appsettings.json; the served
    ///     file's path is <c>/</c>.
    ///     Red: the same in appsettings.json, back to <c>http://localhost:8081/</c>; the project file's path is <c>/</c>.
    /// </summary>
    [Theory]
    [InlineData("src/Daedalus.Web/wwwroot/appsettings.json")]
    [InlineData("src/Daedalus.Web/appsettings.json")]
    public void Sign_out_returns_through_the_logout_callback(string file)
    {
        var oidc = Settings(file).GetProperty("Oidc");

        new Uri(oidc.GetProperty("PostLogoutRedirectUri").GetString()!).AbsolutePath
            .Should().Be("/authentication/logout-callback");
    }

    /// <summary>
    ///     The realm's console client accepts the logout callback as a post-logout address: the same set as its redirect
    ///     addresses, which hold <c>http://localhost:*</c>.
    ///     Red: the client's <c>post.logout.redirect.uris</c> attribute removed; it is missing.
    /// </summary>
    [Fact]
    public void The_realm_accepts_the_logout_callback()
    {
        var client = Settings("keycloak-realm.json").GetProperty("clients").EnumerateArray()
            .Single(c => string.Equals(c.GetProperty("clientId").GetString(), "daedalus-wasm", StringComparison.Ordinal));

        var postLogout = client.TryGetProperty("attributes", out var attributes)
            && attributes.TryGetProperty("post.logout.redirect.uris", out var value)
                ? value.GetString()
                : null;
        postLogout.Should().Be("+", "the post-logout addresses are the redirect addresses");
        client.GetProperty("redirectUris").EnumerateArray().Select(u => u.GetString())
            .Should().Contain("http://localhost:*");
    }

    private static JsonElement Settings(string relativePath) => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), relativePath)),
        new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }).RootElement;

    /// <summary>Walks up from <see cref="AppContext.BaseDirectory"/> to the folder holding <c>Daedalus.sln</c>.</summary>
    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Daedalus.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException($"Could not find Daedalus.sln walking up from {AppContext.BaseDirectory}.");
    }
}

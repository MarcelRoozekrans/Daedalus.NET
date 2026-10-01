using System.Text.Json;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Phase 2.5 task B3, ruling R11: the shipped <c>keycloak-realm.json</c> defines the <c>developer</c> realm role,
///     gives it to user <c>dev</c>, and pins the ids of <c>dev</c> and <c>admin</c>. Checked by parsing the file,
///     because no endpoint test can fail here: <c>HeaderTestAuthHandler</c> takes roles from a header and never reads
///     the realm.
/// </summary>
public sealed class KeycloakRealmTests
{
    [Fact]
    public void The_realm_defines_a_developer_role_and_dev_holds_it()
    {
        var realm = Realm();
        realm.GetProperty("roles").GetProperty("realm").EnumerateArray()
            .Select(r => r.GetProperty("name").GetString()).Should().Contain("developer");
        User(realm, "dev").GetProperty("realmRoles").EnumerateArray()
            .Select(r => r.GetString()).Should().Contain("developer", "the WorkflowResume policy admits developer or admin, and dev is the developer");
    }

    /// <summary>
    ///     The run records its starter's <c>sub</c>, which Keycloak sets to the user's id. A user imported without an
    ///     id gets a fresh one on every realm re-import, so the same person would appear as a different starter. A
    ///     missing <c>id</c> reads as <see langword="null"/>, so removing it fails the assertion rather than throwing.
    /// </summary>
    [Theory]
    [InlineData("dev", "8f0b6c3e-2d4a-4c7e-9b1f-0a5d7e3c1b21")]
    [InlineData("admin", "3c9e7a14-5b2d-4f8a-a6c0-7d1e2f4b9a53")]
    public void Dev_and_admin_have_fixed_ids_so_sub_survives_a_realm_reimport(string username, string id)
    {
        var user = User(Realm(), username);
        var actual = user.TryGetProperty("id", out var value) ? value.GetString() : null;
        actual.Should().Be(id);
    }

    private static JsonElement Realm() => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "keycloak-realm.json")),
        new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }).RootElement;

    private static JsonElement User(JsonElement realm, string username) =>
        realm.GetProperty("users").EnumerateArray().Single(u => string.Equals(u.GetProperty("username").GetString(), username, StringComparison.Ordinal));

    /// <summary>Walks up from <see cref="AppContext.BaseDirectory"/> to the folder holding <c>Daedalus.sln</c>, as <c>CleanArchitectureTests.FindRepositoryRoot</c> does.</summary>
    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Daedalus.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"Could not find Daedalus.sln walking up from {AppContext.BaseDirectory}.");
    }
}

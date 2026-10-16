using Daedalus.Application.DTOs;
using Daedalus.Web.Services;
using Daedalus.Web.State.Repositories;
using Radzen;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     What the Git Repositories page shows for each write's outcome, word for word as it always did, whether it reloads
///     the list after it, and the copies its form edits.
/// </summary>
public sealed class RepositoriesViewTests
{
    private const string Answered = "The server answered 500 Internal Server Error.";

    /// <summary>
    ///     Each saved write says so in its own words, at Success level.
    ///     Red: word every save as the create's; the update, delete and test say "Repository added successfully.".
    ///     Red: notify a save at Info level; the severity is wrong.
    /// </summary>
    [Theory]
    [InlineData(RepositoryWrite.Created, "Created", "Repository added successfully.")]
    [InlineData(RepositoryWrite.Updated, "Updated", "Repository updated successfully.")]
    [InlineData(RepositoryWrite.Deleted, "Deleted", "Repository removed.")]
    [InlineData(RepositoryWrite.Tested, "Success", "Connection test passed.")]
    public void A_save_says_so(RepositoryWrite write, string summary, string detail)
    {
        RepositoriesView.Notice(new RepositorySave(write, null))
            .Should().Be(new RepositoryNotice(NotificationSeverity.Success, summary, detail));
    }

    /// <summary>
    ///     A refused write says what failed and the status the server answered, at Error level.
    ///     Red: drop the reason from a refused write's detail; it reads "Failed to create repository." alone.
    ///     Red: word every refusal "Failed to create repository."; the update and delete are misnamed.
    /// </summary>
    [Theory]
    [InlineData(RepositoryWrite.Created, "Failed to create repository.")]
    [InlineData(RepositoryWrite.Updated, "Failed to update repository.")]
    [InlineData(RepositoryWrite.Deleted, "Failed to delete repository.")]
    public void A_refused_write_says_what_failed_and_the_status(RepositoryWrite write, string what)
    {
        RepositoriesView.Notice(new RepositorySave(write, Answered))
            .Should().Be(new RepositoryNotice(NotificationSeverity.Error, "Error", $"{what} {Answered}"));
    }

    /// <summary>
    ///     A write that got no answer says only what failed, as the page's catch-all always did.
    ///     Red: append the reason in the no-answer arm too; the detail ends with "The request got no answer.".
    /// </summary>
    [Theory]
    [InlineData(RepositoryWrite.Created, "Failed to create repository.")]
    [InlineData(RepositoryWrite.Updated, "Failed to update repository.")]
    [InlineData(RepositoryWrite.Deleted, "Failed to delete repository.")]
    public void A_write_with_no_answer_says_only_what_failed(RepositoryWrite write, string what)
    {
        RepositoriesView.Notice(new RepositorySave(write, RepositoriesApiClient.NoAnswer))
            .Should().Be(new RepositoryNotice(NotificationSeverity.Error, "Error", what));
    }

    /// <summary>
    ///     A connection test the server refused is a Warning that the test failed; one that got no answer is an Error
    ///     that it could not be tested.
    ///     Red: drop the Warning arm for a refused test; it reads "Could not test connection." at Error.
    ///     Red: word a test with no answer as a refused test; it is a Warning.
    /// </summary>
    [Fact]
    public void A_failed_connection_test_says_whether_the_server_answered()
    {
        RepositoriesView.Notice(new RepositorySave(RepositoryWrite.Tested, Answered))
            .Should().Be(new RepositoryNotice(NotificationSeverity.Warning, "Failed", "Connection test failed."));
        RepositoriesView.Notice(new RepositorySave(RepositoryWrite.Tested, RepositoriesApiClient.NoAnswer))
            .Should().Be(new RepositoryNotice(NotificationSeverity.Error, "Error", "Could not test connection."));
    }

    /// <summary>
    ///     The page reloads the list after a write the server answered, saved or refused, and not after one that got no
    ///     answer, nor after a connection test, as it always did.
    ///     Red: Reloads is always true; a write with no answer and a test reload.
    ///     Red: reload only after a save; a refused write does not.
    /// </summary>
    [Theory]
    [InlineData(RepositoryWrite.Created, null, true)]
    [InlineData(RepositoryWrite.Updated, Answered, true)]
    [InlineData(RepositoryWrite.Deleted, RepositoriesApiClient.NoAnswer, false)]
    [InlineData(RepositoryWrite.Tested, null, false)]
    [InlineData(RepositoryWrite.Tested, Answered, false)]
    public void The_page_reloads_after_a_write_the_server_answered(RepositoryWrite write, string? refusal, bool reloads)
    {
        RepositoriesView.Reloads(new RepositorySave(write, refusal)).Should().Be(reloads);
    }

    /// <summary>
    ///     The form edits a copy of the repository in the state, never the state's own: a cancelled edit leaves the card
    ///     as it was.
    ///     Red: For returns the repository itself; the state's name changes with the draft's.
    ///     Red: For copies no fields; the draft's name is empty.
    /// </summary>
    [Fact]
    public void The_form_edits_a_copy()
    {
        var held = new RepositoryConfigurationDto { Id = Guid.NewGuid(), Name = "atlas", Url = "https://x/atlas", IsActive = false };

        var draft = RepositoryDrafts.For(held);
        draft.Should().Be(held);
        draft.Name = "renamed";

        held.Name.Should().Be("atlas", "the state's repository is not the form's");
    }

    /// <summary>
    ///     A new repository's draft starts on GitHub and main, with an id of its own, as the Add form always did.
    ///     Red: New sets no id; it is Guid.Empty.
    ///     Red: New starts on GitLab; the platform is wrong.
    /// </summary>
    [Fact]
    public void A_new_draft_starts_on_github_and_main()
    {
        var draft = RepositoryDrafts.New();

        draft.Id.Should().NotBeEmpty();
        draft.Platform.Should().Be("GitHub");
        draft.DefaultBranch.Should().Be("main");
    }

    private static readonly RepositoryConfigurationDto Filled = new()
    {
        Id = Guid.NewGuid(),
        Name = "atlas",
        Url = "https://github.com/acme/atlas",
        Platform = "GitLab",
        DefaultBranch = "trunk",
        AuthenticationMethod = "PAT",
        CredentialIdentifier = "vault-key",
        IsActive = false,
        Description = "The atlas repository",
    };

    /// <summary>
    ///     The create request carries every field the form edits.
    ///     Red: drop any one field from ToCreate; the request misses it.
    /// </summary>
    [Fact]
    public void The_create_request_carries_the_draft()
    {
        RepositoryDrafts.ToCreate(Filled).Should().Be(new CreateRepositoryConfigurationDto
        {
            Name = "atlas",
            Url = "https://github.com/acme/atlas",
            Platform = "GitLab",
            DefaultBranch = "trunk",
            AuthenticationMethod = "PAT",
            CredentialIdentifier = "vault-key",
            Description = "The atlas repository",
        });
    }

    /// <summary>
    ///     The update request carries every field the form edits, Active among them.
    ///     Red: drop any one field from ToUpdate; the request misses it.
    /// </summary>
    [Fact]
    public void The_update_request_carries_the_draft()
    {
        RepositoryDrafts.ToUpdate(Filled with { IsActive = true }).Should().Be(new UpdateRepositoryConfigurationDto
        {
            Name = "atlas",
            Url = "https://github.com/acme/atlas",
            DefaultBranch = "trunk",
            AuthenticationMethod = "PAT",
            CredentialIdentifier = "vault-key",
            IsActive = true,
            Description = "The atlas repository",
        });
    }
}

using Daedalus.Domain.CodeAnalysis;

namespace Daedalus.Tests.Unit.Domain.CodeAnalysis;

/// <summary>
///     Unit tests for <see cref="CodeAnalysisRequest.Rehydrate"/>, <see cref="AnalysisIteration.Rehydrate"/> and the
///     <c>Rehydrate</c> of the three owned value objects.
/// </summary>
public sealed class CodeAnalysisRehydrateTests
{
    private static readonly DateTime _at = new(2026, 11, 5, 9, 15, 30, DateTimeKind.Utc);

    /// <summary>
    ///     A stored request loads as it is, even one <see cref="CodeAnalysisRequest.Create"/> would refuse today: an
    ///     empty title and zero max iterations; with every other value, its row version and its iterations kept in the
    ///     order given. Red: validate in <c>Rehydrate</c>, or drop any one assignment.
    /// </summary>
    [Fact]
    public void Request_Rehydrate_keeps_every_value_including_ones_Create_rejects()
    {
        CodeAnalysisRequest.Create(Guid.NewGuid(), "", "d", "https://github.com/org/repo", 0).IsFailure.Should()
            .BeTrue("the title and limit below are ones Create refuses");
        var id = Guid.NewGuid();
        var createdBy = Guid.NewGuid();
        var assignedTo = Guid.NewGuid();
        var repository = RepositoryReference.Rehydrate("u", "b", "c", RepositoryPlatform.GitLab);
        var location = CodeLocation.Rehydrate("f", 1, 2);
        var outcome = AnalysisOutcome.Rehydrate("pr", "sha", "{}", true);
        var first = AnalysisIteration.Rehydrate(id, id, 2, null, null, "p2", "r2", null, null, null, 0, 0, null, _at);
        var second = AnalysisIteration.Rehydrate(Guid.NewGuid(), id, 1, null, null, "p1", "r1", null, null, null, 0, 0,
            null, _at);

        var request = CodeAnalysisRequest.Rehydrate(
            id, "", "description", repository, location, AnalysisType.BugFix, 7, "[1]", "[2]", "[3]", "/tree", "branch",
            AnalysisStatus.ValidationFailed, 4, 0, "goals", "prompt", "response", outcome, _at, _at.AddMinutes(1),
            _at.AddMinutes(2), _at.AddMinutes(3), "reason", createdBy, assignedTo, 42u, [first, second]);

        request.Id.Should().Be(id);
        request.Title.Should().BeEmpty();
        request.Description.Should().Be("description");
        request.Repository.Should().BeSameAs(repository);
        request.Location.Should().BeSameAs(location);
        request.Type.Should().Be(AnalysisType.BugFix);
        request.Priority.Should().Be(7);
        request.Requirements.Should().Be("[1]");
        request.ReferencedDocuments.Should().Be("[2]");
        request.ExternalReferences.Should().Be("[3]");
        request.LocalWorkTreePath.Should().Be("/tree");
        request.FeatureBranchName.Should().Be("branch");
        request.Status.Should().Be(AnalysisStatus.ValidationFailed);
        request.CurrentIteration.Should().Be(4);
        request.MaxIterations.Should().Be(0);
        request.CompletionPromise.Should().Be("goals");
        request.LastPromptSent.Should().Be("prompt");
        request.LastAiResponse.Should().Be("response");
        request.Outcome.Should().BeSameAs(outcome);
        request.CreatedAt.Should().Be(_at);
        request.StartedAt.Should().Be(_at.AddMinutes(1));
        request.CompletedAt.Should().Be(_at.AddMinutes(2));
        request.CancelledAt.Should().Be(_at.AddMinutes(3));
        request.FailureReason.Should().Be("reason");
        request.CreatedBy.Should().Be(createdBy);
        request.AssignedTo.Should().Be(assignedTo);
        request.RowVersion.Should().Be(42u);
        request.Iterations.Should().Equal(first, second);
    }

    /// <summary>
    ///     A stored iteration loads as it is, even one <see cref="AnalysisIteration.Create"/> would refuse today: an
    ///     empty request id, iteration 0 and a blank prompt; untrimmed. Red: validate in <c>Rehydrate</c>, or drop any
    ///     one assignment.
    /// </summary>
    [Fact]
    public void Iteration_Rehydrate_keeps_every_value_including_ones_Create_rejects()
    {
        AnalysisIteration.Create(Guid.NewGuid(), Guid.Empty, 0, " ", "r").IsFailure.Should()
            .BeTrue("the values below are ones Create refuses");
        var id = Guid.NewGuid();

        var iteration = AnalysisIteration.Rehydrate(
            id, Guid.Empty, 0, " branch ", "sha", " ", "response", true, false, "[]", 11, 22, "model", _at);

        iteration.Id.Should().Be(id);
        iteration.CodeAnalysisRequestId.Should().Be(Guid.Empty);
        iteration.IterationNumber.Should().Be(0);
        iteration.BranchName.Should().Be(" branch ");
        iteration.CommitSha.Should().Be("sha");
        iteration.PromptSent.Should().Be(" ");
        iteration.AiResponse.Should().Be("response");
        iteration.CompilationSucceeded.Should().BeTrue();
        iteration.TestsPassed.Should().BeFalse();
        iteration.ValidationErrors.Should().Be("[]");
        iteration.InputTokens.Should().Be(11);
        iteration.OutputTokens.Should().Be(22);
        iteration.ModelId.Should().Be("model");
        iteration.CreatedAt.Should().Be(_at);
    }

    /// <summary>
    ///     A stored reference keeps its platform, which <see cref="RepositoryReference.Create"/> would detect again from
    ///     the URL, and its untrimmed values. Red: detect the platform or trim in <c>Rehydrate</c>.
    /// </summary>
    [Fact]
    public void Reference_Rehydrate_keeps_the_stored_platform_and_values()
    {
        RepositoryReference.Create("https://github.com/org/repo").Value.Platform.Should().Be(RepositoryPlatform.GitHub);

        var reference = RepositoryReference.Rehydrate(" https://github.com/org/repo ", " main ", "sha", RepositoryPlatform.None);

        reference.Url.Should().Be(" https://github.com/org/repo ");
        reference.Branch.Should().Be(" main ");
        reference.CommitSha.Should().Be("sha");
        reference.Platform.Should().Be(RepositoryPlatform.None);
    }

    /// <summary>
    ///     A stored location loads even with a start line after its end line, which <see cref="CodeLocation.Create"/>
    ///     refuses. Red: validate in <c>Rehydrate</c>, or drop any one assignment.
    /// </summary>
    [Fact]
    public void Location_Rehydrate_keeps_a_range_Create_rejects()
    {
        CodeLocation.Create("f", 9, 3).IsFailure.Should().BeTrue("the range below is one Create refuses");

        var location = CodeLocation.Rehydrate(" f ", 9, 3);

        location.FilePath.Should().Be(" f ");
        location.StartLine.Should().Be(9);
        location.EndLine.Should().Be(3);
    }

    /// <summary>A stored outcome keeps every value. Red: drop any one assignment in <c>Rehydrate</c>.</summary>
    [Fact]
    public void Outcome_Rehydrate_keeps_every_value()
    {
        var outcome = AnalysisOutcome.Rehydrate("pr", "sha", "{}", true);

        outcome.PullRequestUrl.Should().Be("pr");
        outcome.CommitShaFinal.Should().Be("sha");
        outcome.ValidationResult.Should().Be("{}");
        outcome.HasFailedValidation.Should().BeTrue();
    }

    /// <summary>A request created in memory has no row version until it is read. Red: give it any other default.</summary>
    [Fact]
    public void A_created_request_has_no_row_version()
    {
        CodeAnalysisRequest.Create(Guid.NewGuid(), "t", "d", "https://github.com/org/repo", 3).Value.RowVersion.Should()
            .Be(0u);
    }
}

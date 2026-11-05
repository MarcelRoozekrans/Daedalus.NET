using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>Unit tests for <see cref="RepositoryConfiguration.Rehydrate"/>.</summary>
public sealed class RepositoryConfigurationRehydrateTests
{
    private static readonly DateTime _at = new(2026, 11, 5, 9, 15, 30, DateTimeKind.Utc);

    /// <summary>
    ///     A stored configuration loads as it is, even one <see cref="RepositoryConfiguration.Create"/> would refuse
    ///     today: a blank name and URL, a platform outside the list, an untrimmed branch, an inactive row with every
    ///     optional field set. Red: validate in <c>Rehydrate</c>, or drop any one assignment.
    /// </summary>
    [Fact]
    public void Rehydrate_keeps_every_value_including_ones_Create_rejects()
    {
        var id = Guid.NewGuid();
        RepositoryConfiguration.Create(id, " ", " ", "Unknown").IsFailure.Should()
            .BeTrue("the values below are ones Create refuses");

        var repository = RepositoryConfiguration.Rehydrate(
            id, " ", " ", "Unknown", " dev ", " PAT ", " vault://k ", false, " d ", _at, _at.AddHours(1), _at.AddHours(2));

        repository.Id.Should().Be(id);
        repository.Name.Should().Be(" ");
        repository.Url.Should().Be(" ");
        repository.Platform.Should().Be("Unknown");
        repository.DefaultBranch.Should().Be(" dev ");
        repository.AuthenticationMethod.Should().Be(" PAT ");
        repository.CredentialIdentifier.Should().Be(" vault://k ");
        repository.IsActive.Should().BeFalse();
        repository.Description.Should().Be(" d ");
        repository.CreatedAt.Should().Be(_at);
        repository.ModifiedAt.Should().Be(_at.AddHours(1));
        repository.LastUsedAt.Should().Be(_at.AddHours(2));
    }
}

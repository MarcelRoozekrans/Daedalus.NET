using AwesomeAssertions.Equivalency;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Repositories;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SystemTask = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Repositories;

/// <summary>
///     Phase 3.5: the repository configuration repository on ZeroAlloc.ORM stores and loads every column, loads a row
///     whatever <c>RepositoryConfiguration.Create</c> would say of it today, overwrites exactly the columns its request
///     carries with no version check, and answers a row deleted under an update with the retry text.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class RepositoryConfigurationRepositoryRoundTripTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTime _at = new(2026, 11, 5, 9, 15, 30, DateTimeKind.Utc);

    public async SystemTask InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public SystemTask DisposeAsync() => SystemTask.CompletedTask;

    private RepositoryConfigurationRepository Repository() =>
        new(fixture.Db, NullLogger<RepositoryConfigurationRepository>.Instance);

    /// <summary>
    ///     A configuration stored through the repository, with every optional field set, comes back equal in every
    ///     property with a UTC creation time, through the get and through the list. Red, shown once: read
    ///     <c>"Description"</c> into <c>CredentialIdentifier</c> in the row query, and the comparison names it.
    /// </summary>
    [Fact]
    public async SystemTask Stored_repository_loads_back_whole()
    {
        var stored = RepositoryConfiguration.Create(
            Guid.NewGuid(), "Name", "https://example.com/r.git", "GitLab", "develop", "PAT", "vault://token",
            "Described", _at).Value;
        (await Repository().AddAsync(stored, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var loaded = (await Repository().GetByIdAsync(stored.Id, CancellationToken.None)).Value;
        var listed = (await Repository().GetAllAsync(CancellationToken.None)).Value;

        loaded.Should().BeEquivalentTo(stored, Members);
        loaded.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        listed.Should().ContainSingle().Which.Should().BeEquivalentTo(stored, Members);
    }

    /// <summary>
    ///     Review focus 1: a row <c>Create</c> refuses, a blank name and URL, a platform outside the list, an untrimmed
    ///     branch, with a modification and a last-use time, still loads as written, through the get and the list. Red:
    ///     build the loaded row through <c>Create</c>, and the load fails.
    /// </summary>
    [Fact]
    public async SystemTask A_row_Create_rejects_still_loads()
    {
        var id = Guid.NewGuid();
        RepositoryConfiguration.Create(id, " ", " ", "Unknown").IsFailure.Should().BeTrue("the row below is one Create refuses");
        await ExecuteAsync(
            """
            INSERT INTO "RepositoryConfigurations"
                ("Id", "Name", "Url", "Platform", "DefaultBranch", "AuthenticationMethod", "CredentialIdentifier",
                 "IsActive", "Description", "CreatedAt", "ModifiedAt", "LastUsedAt")
            VALUES (@id, ' ', ' ', 'Unknown', ' dev ', ' PAT ', ' vault://k ', false, ' d ', @at, @modifiedAt, @usedAt)
            """,
            ("id", id), ("at", _at), ("modifiedAt", _at.AddHours(1)), ("usedAt", _at.AddHours(2)));
        var expected = RepositoryConfiguration.Rehydrate(
            id, " ", " ", "Unknown", " dev ", " PAT ", " vault://k ", false, " d ", _at, _at.AddHours(1),
            _at.AddHours(2));

        var loaded = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        var listed = (await Repository().GetAllAsync(CancellationToken.None)).Value;

        loaded.Should().BeEquivalentTo(expected, Members);
        listed.Should().ContainSingle().Which.Should().BeEquivalentTo(expected, Members);
    }

    /// <summary>
    ///     The list is newest first, then by id descending for equal times. Red: order ascending.
    /// </summary>
    [Fact]
    public async SystemTask The_list_is_newest_first_then_by_id()
    {
        var ids = new List<Guid>();
        foreach (var at in new[] { _at, _at.AddHours(1), _at.AddHours(1) })
        {
            var repository = RepositoryConfiguration.Create(Guid.NewGuid(), "R", "https://example.com/r.git", createdAt: at).Value;
            (await Repository().AddAsync(repository, CancellationToken.None)).IsSuccess.Should().BeTrue();
            ids.Add(repository.Id);
        }

        var listed = (await Repository().GetAllAsync(CancellationToken.None)).Value;

        var newest = new[] { ids[1], ids[2] }.OrderDescending().ToArray();
        listed.Select(r => r.Id).Should().Equal(newest[0], newest[1], ids[0]);
    }

    /// <summary>
    ///     The insert needs no <c>"RowVersion"</c>: the dead <c>bytea</c> column stays NULL, as EF left it. Red: bind a
    ///     value to it in the insert; the column is no longer NULL.
    /// </summary>
    [Fact]
    public async SystemTask Add_leaves_the_dead_RowVersion_column_null()
    {
        var stored = RepositoryConfiguration.Create(Guid.NewGuid(), "R", "https://example.com/r.git").Value;
        (await Repository().AddAsync(stored, CancellationToken.None)).IsSuccess.Should().BeTrue();

        (await ScalarAsync("""SELECT "RowVersion" IS NULL FROM "RepositoryConfigurations" WHERE "Id" = @id""", ("id", stored.Id)))
            .Should().Be(true);
    }

    /// <summary>
    ///     The overwrite writes every column the request carries, the name, URL, branch, authentication method,
    ///     credential identifier, active flag, description and modification time, and not the platform, the creation
    ///     time or the last-use time. Red, once: leave <c>Description</c> out of the update; the stored description
    ///     keeps its old value. Red, once: also set <c>Platform</c>; the stored platform changes.
    /// </summary>
    [Fact]
    public async SystemTask UpdateAsync_overwrites_the_columns_the_request_carries_and_nothing_else()
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO "RepositoryConfigurations"
                ("Id", "Name", "Url", "Platform", "DefaultBranch", "AuthenticationMethod", "CredentialIdentifier",
                 "IsActive", "Description", "CreatedAt", "LastUsedAt")
            VALUES (@id, 'Old', 'https://example.com/old.git', 'GitLab', 'old', 'PAT', 'vault://old', true, 'Old', @at, @usedAt)
            """,
            ("id", id), ("at", _at), ("usedAt", _at.AddHours(2)));
        var read = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        read.Update("New", "https://example.com/new.git", "develop", "SSH", null, false, "Described").IsSuccess
            .Should().BeTrue();

        (await Repository().UpdateAsync(read, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var stored = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        stored.Name.Should().Be("New");
        stored.Url.Should().Be("https://example.com/new.git");
        stored.DefaultBranch.Should().Be("develop");
        stored.AuthenticationMethod.Should().Be("SSH");
        stored.CredentialIdentifier.Should().BeNull();
        stored.IsActive.Should().BeFalse();
        stored.Description.Should().Be("Described");
        stored.ModifiedAt.Should().BeCloseTo(read.ModifiedAt!.Value, TimeSpan.FromMilliseconds(1));
        stored.Platform.Should().Be("GitLab");
        stored.CreatedAt.Should().Be(_at);
        stored.LastUsedAt.Should().Be(_at.AddHours(2));
    }

    /// <summary>
    ///     Owner decision 5: the overwrite is independent. Two reads of one configuration, both saved: both succeed and
    ///     the second's values stand, as they did under EF's inert token. Red: guard the update on <c>xmin</c>; the
    ///     second save is refused.
    /// </summary>
    [Fact]
    public async SystemTask UpdateAsync_is_independent_the_second_save_overwrites_the_first()
    {
        var stored = RepositoryConfiguration.Create(Guid.NewGuid(), "R", "https://example.com/r.git").Value;
        (await Repository().AddAsync(stored, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var first = (await Repository().GetByIdAsync(stored.Id, CancellationToken.None)).Value;
        var second = (await Repository().GetByIdAsync(stored.Id, CancellationToken.None)).Value;
        first.Update("First", "https://example.com/first.git", "main", null, null, true, null).IsSuccess.Should().BeTrue();
        second.Update("Second", "https://example.com/second.git", "main", null, null, true, null).IsSuccess
            .Should().BeTrue();

        (await Repository().UpdateAsync(first, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await Repository().UpdateAsync(second, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var reloaded = (await Repository().GetByIdAsync(stored.Id, CancellationToken.None)).Value;
        reloaded.Name.Should().Be("Second");
        reloaded.Url.Should().Be("https://example.com/second.git");
    }

    /// <summary>
    ///     Review focus 3: a configuration deleted after its load keeps today's answer to its update, "modified by
    ///     another operation", where EF's update matched no row; shown green against EF Core before the port. Red:
    ///     answer success when the update matches no row; the deleted configuration answers success.
    /// </summary>
    [Fact]
    public async SystemTask UpdateAsync_of_a_row_deleted_since_its_load_answers_the_retry_text()
    {
        var stored = RepositoryConfiguration.Create(Guid.NewGuid(), "R", "https://example.com/r.git").Value;
        (await Repository().AddAsync(stored, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var read = (await Repository().GetByIdAsync(stored.Id, CancellationToken.None)).Value;
        read.Update("Renamed", "https://example.com/r.git", "main", null, null, true, null).IsSuccess.Should().BeTrue();
        await ExecuteAsync("""DELETE FROM "RepositoryConfigurations" WHERE "Id" = @id""", ("id", stored.Id));

        var result = await Repository().UpdateAsync(read, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(RepositoryConfigurationRepository.ModifiedByAnotherOperation)
            .And.Be("The repository was modified by another operation. Please retry.");
        (await Repository().GetByIdAsync(stored.Id, CancellationToken.None)).Error
            .Should().Be($"Repository configuration {stored.Id} not found");
    }

    private static Func<EquivalencyOptions<RepositoryConfiguration>, EquivalencyOptions<RepositoryConfiguration>> Members =>
        o => o.ComparingByMembers<RepositoryConfiguration>();

    private async SystemTask ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private async System.Threading.Tasks.Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync();
    }
}

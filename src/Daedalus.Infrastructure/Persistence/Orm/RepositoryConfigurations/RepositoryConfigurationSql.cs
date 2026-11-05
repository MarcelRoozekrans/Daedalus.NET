using System.Data.Async;
using ZeroAlloc.ORM;
using ZeroAlloc.ORM.Linq;
using RepositoryConfiguration = Daedalus.Domain.Entities.RepositoryConfiguration;

namespace Daedalus.Infrastructure.Persistence.Orm.RepositoryConfigurations;

/// <summary>
///     The SQL of the <c>"RepositoryConfigurations"</c> table over one open connection. Its public methods take and
///     return <see cref="RepositoryConfiguration"/>; the generated queries, their companions and the row record stay
///     inside.
/// </summary>
/// <remarks>
///     The update is an independent full overwrite of the columns the request carries, with no version check: the dead
///     <c>bytea</c> token never worked, and the request replaces the fields whatever another write set (owner decision
///     5). It throws <see cref="ZeroAllocOrmConcurrencyException"/> when it matches no row, a configuration deleted
///     since it was read. The insert is raw SQL, as the ORM has no LINQ insert (ruling R10), and lists every live
///     column; the dead <c>"RowVersion"</c> stays NULL, as EF left it.
/// </remarks>
public sealed partial class RepositoryConfigurationSql(IAsyncDbConnection connection)
{
    private const string InsertSql = """
        INSERT INTO "RepositoryConfigurations"
            ("Id", "Name", "Url", "Platform", "DefaultBranch", "AuthenticationMethod", "CredentialIdentifier",
             "IsActive", "Description", "CreatedAt", "ModifiedAt", "LastUsedAt")
        VALUES (@id, @name, @url, @platform, @defaultBranch, @authenticationMethod, @credentialIdentifier,
                @isActive, @description, @createdAt, @modifiedAt, @lastUsedAt)
        """;

    /// <summary>Inserts <paramref name="repository"/> with every live column as given.</summary>
    /// <param name="repository">The configuration to store.</param>
    /// <param name="ct">Cancels the insert.</param>
    /// <returns>A task that completes when the row is written.</returns>
    public async Task InsertAsync(RepositoryConfiguration repository, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repository);

        await Insert(
            repository.Id, repository.Name, repository.Url, repository.Platform, repository.DefaultBranch,
            repository.AuthenticationMethod, repository.CredentialIdentifier, repository.IsActive,
            repository.Description, repository.CreatedAt, repository.ModifiedAt, repository.LastUsedAt, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Reads the configuration with <paramref name="id"/>.</summary>
    /// <param name="id">The configuration id.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The configuration as stored, or null when there is none.</returns>
    public async Task<RepositoryConfiguration?> FindAsync(Guid id, CancellationToken ct)
    {
        var row = await FindRow(id, ct).ConfigureAwait(false);
        return row?.ToDomain();
    }

    /// <summary>Reads every configuration, newest first.</summary>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The configurations as stored, ordered by <c>CreatedAt</c> then id, both descending.</returns>
    public async Task<IReadOnlyList<RepositoryConfiguration>> ListAsync(CancellationToken ct)
    {
        var rows = await ListRows(ct).ConfigureAwait(false);
        return rows.ConvertAll(row => row.ToDomain());
    }

    /// <summary>
    ///     Overwrites the name, URL, default branch, authentication method, credential identifier, active flag,
    ///     description and modification time, the columns <c>RepositoryConfiguration.Update</c> changes, whatever else
    ///     was written since the configuration was read.
    /// </summary>
    /// <param name="repository">The configuration as changed.</param>
    /// <param name="ct">Cancels the update.</param>
    /// <returns>A task that completes when the row is written.</returns>
    /// <exception cref="ZeroAllocOrmConcurrencyException">No row has the configuration's id.</exception>
    public Task UpdateAsync(RepositoryConfiguration repository, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return Update(
            repository.Id, repository.Name, repository.Url, repository.DefaultBranch, repository.AuthenticationMethod,
            repository.CredentialIdentifier, repository.IsActive, repository.Description, repository.ModifiedAt, ct);
    }

    /// <summary>Deletes the configuration's row.</summary>
    /// <param name="id">The configuration id.</param>
    /// <param name="ct">Cancels the delete.</param>
    /// <returns>The number of rows deleted, 0 when none was stored.</returns>
    public Task<int> DeleteAsync(Guid id, CancellationToken ct) => Delete(id, ct);

    [Command(InsertSql)]
    private partial Task<int> Insert(
        Guid id, string name, string url, string platform, string defaultBranch, string? authenticationMethod,
        string? credentialIdentifier, bool isActive, string? description, DateTime createdAt, DateTime? modifiedAt,
        DateTime? lastUsedAt, CancellationToken ct);

    [LinqQuery(nameof(FindRowQuery))]
    private partial Task<RepositoryConfigurationRow?> FindRow(Guid id, CancellationToken ct);

    private static IQuery<RepositoryConfigurationRow> FindRowQuery(Guid id) =>
        Sql.Table<RepositoryConfiguration>()
            .Where(r => r.Id == id)
            .Select(r => new RepositoryConfigurationRow(
                r.Id, r.Name, r.Url, r.Platform, r.DefaultBranch, r.AuthenticationMethod, r.CredentialIdentifier,
                r.IsActive, r.Description, r.CreatedAt, r.ModifiedAt, r.LastUsedAt));

    [LinqQuery(nameof(ListRowsQuery))]
    private partial Task<List<RepositoryConfigurationRow>> ListRows(CancellationToken ct);

    private static IQuery<RepositoryConfigurationRow> ListRowsQuery() =>
        Sql.Table<RepositoryConfiguration>()
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Select(r => new RepositoryConfigurationRow(
                r.Id, r.Name, r.Url, r.Platform, r.DefaultBranch, r.AuthenticationMethod, r.CredentialIdentifier,
                r.IsActive, r.Description, r.CreatedAt, r.ModifiedAt, r.LastUsedAt));

    [LinqCommand(nameof(UpdateQuery), ThrowOnNoRows = true)]
    private partial Task Update(
        Guid id, string name, string url, string defaultBranch, string? authenticationMethod,
        string? credentialIdentifier, bool isActive, string? description, DateTime? modifiedAt, CancellationToken ct);

    private static int UpdateQuery(
        Guid id, string name, string url, string defaultBranch, string? authenticationMethod,
        string? credentialIdentifier, bool isActive, string? description, DateTime? modifiedAt) =>
        Sql.Table<RepositoryConfiguration>()
            .Where(r => r.Id == id)
            .ExecuteUpdate(set => set
                .SetProperty(r => r.Name, name)
                .SetProperty(r => r.Url, url)
                .SetProperty(r => r.DefaultBranch, defaultBranch)
                .SetProperty(r => r.AuthenticationMethod, authenticationMethod)
                .SetProperty(r => r.CredentialIdentifier, credentialIdentifier)
                .SetProperty(r => r.IsActive, isActive)
                .SetProperty(r => r.Description, description)
                .SetProperty(r => r.ModifiedAt, modifiedAt));

    [LinqCommand(nameof(DeleteQuery))]
    private partial Task<int> Delete(Guid id, CancellationToken ct);

    private static int DeleteQuery(Guid id) =>
        Sql.Table<RepositoryConfiguration>().Where(r => r.Id == id).ExecuteDelete();
}

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     Collection definition for Aspire-based database integration tests.
///     Tests in this collection share the same PostgreSQL container managed by Aspire.
/// </summary>
[CollectionDefinition(Name)]
public class AspireDatabaseCollection : ICollectionFixture<AspirePostgresFixture>
{
    public const string Name = "Aspire Database";
}

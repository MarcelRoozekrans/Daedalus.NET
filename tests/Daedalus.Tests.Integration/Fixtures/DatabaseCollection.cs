namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     Collection definition for database integration tests.
///     Tests in this collection share the same PostgreSQL container.
/// </summary>
[CollectionDefinition(Name)]
public class DatabaseCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Database";
}

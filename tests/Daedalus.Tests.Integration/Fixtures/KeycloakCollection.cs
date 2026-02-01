namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     Collection definition for Keycloak integration tests.
///     Tests in this collection share the same Keycloak container instance.
/// </summary>
[CollectionDefinition(Name)]
public class KeycloakCollection : ICollectionFixture<KeycloakFixture>
{
    public const string Name = "Keycloak";
}

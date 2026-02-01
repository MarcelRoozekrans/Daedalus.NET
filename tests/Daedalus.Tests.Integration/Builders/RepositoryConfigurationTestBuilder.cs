using Daedalus.Domain.Entities;
using Daedalus.Tests.Integration.Helpers;

namespace Daedalus.Tests.Integration.Builders;

/// <summary>
///     Fluent builder for creating test RepositoryConfiguration entities.
///     Provides a clean, readable way to set up test repository configuration data.
/// </summary>
/// <example>
///     var repo = new RepositoryConfigurationTestBuilder()
///         .WithName("My Repo")
///         .WithPlatform("GitLab")
///         .Build();
///     var inactiveRepo = new RepositoryConfigurationTestBuilder()
///         .AsInactive()
///         .Build();
/// </example>
public sealed class RepositoryConfigurationTestBuilder
{
    private string? _authenticationMethod;
    private string? _credentialIdentifier;
    private string _defaultBranch = "main";
    private string? _description;
    private Guid _id = Guid.NewGuid();
    private bool _isActive = true;
    private string _name = "Test Repository";
    private string _platform = "GitHub";
    private string _url = "https://github.com/org/repo";

    /// <summary>
    ///     Sets the repository configuration ID.
    /// </summary>
    public RepositoryConfigurationTestBuilder WithId(Guid id)
    {
        _id = id;
        return this;
    }

    /// <summary>
    ///     Sets the repository name.
    /// </summary>
    public RepositoryConfigurationTestBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    /// <summary>
    ///     Sets the repository URL.
    /// </summary>
    public RepositoryConfigurationTestBuilder WithUrl(string url)
    {
        _url = url;
        return this;
    }

    /// <summary>
    ///     Sets the hosting platform.
    /// </summary>
    public RepositoryConfigurationTestBuilder WithPlatform(string platform)
    {
        _platform = platform;
        return this;
    }

    /// <summary>
    ///     Sets the default branch.
    /// </summary>
    public RepositoryConfigurationTestBuilder WithDefaultBranch(string defaultBranch)
    {
        _defaultBranch = defaultBranch;
        return this;
    }

    /// <summary>
    ///     Sets the authentication method.
    /// </summary>
    public RepositoryConfigurationTestBuilder WithAuthentication(string method, string? credentialIdentifier = null)
    {
        _authenticationMethod = method;
        _credentialIdentifier = credentialIdentifier;
        return this;
    }

    /// <summary>
    ///     Sets the description.
    /// </summary>
    public RepositoryConfigurationTestBuilder WithDescription(string description)
    {
        _description = description;
        return this;
    }

    /// <summary>
    ///     Marks the repository configuration as inactive.
    /// </summary>
    public RepositoryConfigurationTestBuilder AsInactive()
    {
        _isActive = false;
        return this;
    }

    /// <summary>
    ///     Builds the RepositoryConfiguration entity with configured values.
    /// </summary>
    /// <returns>A fully configured RepositoryConfiguration entity</returns>
    /// <exception cref="InvalidOperationException">If creation fails</exception>
    public RepositoryConfiguration Build()
    {
        var result = RepositoryConfiguration.Create(
            _id, _name, _url, _platform, _defaultBranch,
            _authenticationMethod, _credentialIdentifier, _description);

        var entity = result.MustSucceed("Failed to build repository configuration for testing");

        if (!_isActive)
        {
            entity.Deactivate().EnsureSuccess("Failed to deactivate repository during build");
        }

        return entity;
    }
}

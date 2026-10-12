using Daedalus.Domain.CodeAnalysis;
using Daedalus.Infrastructure.Services.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daedalus.Tests.Unit.Infrastructure.Services.CodeAnalysis;

/// <summary>Phase 2.11, ruling P30: a token failure's text reaches a run's error, so it carries no exception's text.</summary>
public sealed class RepositoryAuthenticationProviderFailureTests
{
    /// <summary>Red: return <c>$"Error getting auth token: {ex.Message}"</c> as before; the text carries the exception's message.</summary>
    [Fact]
    public void A_failure_to_read_the_token_names_the_platform_and_no_exception_text()
    {
        var result = RepositoryAuthenticationProvider.ReadToken(
            RepositoryPlatform.AzureDevOps, _ => throw new InvalidOperationException("zqx-exception-text"), NullLogger.Instance);

        result.Error.Should().Be("Error getting the auth token for AzureDevOps");
    }
}

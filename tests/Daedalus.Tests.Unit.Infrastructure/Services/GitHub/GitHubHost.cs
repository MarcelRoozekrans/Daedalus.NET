using Daedalus.Infrastructure.Extensions;
using Daedalus.Infrastructure.Services.GitHub;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Daedalus.Tests.Unit.Infrastructure.Services.GitHub;

/// <summary>
///     The real <c>AddGitHubApi</c> registration with <c>primary</c> in place of the network. Retry delays are
///     zero and the attempt timeout is <c>attemptTimeout</c>, so a retry or a timeout takes milliseconds. A standard
///     pipeline's options are named after its client: <c>{client}-standard</c>.
/// </summary>
internal static class GitHubHost
{
    public static ServiceProvider Build(HttpMessageHandler primary, TimeSpan? attemptTimeout = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IGitHubTokenSource>(new GitHubTokenSource(_ => "ghp_example"));
        services.AddGitHubApi(new ConfigurationBuilder().Build());
        services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(() => primary));

        foreach (var client in new[] { GitHubApi.ReadClientName, GitHubApi.WriteClientName })
        {
            services.Configure<HttpStandardResilienceOptions>($"{client}-standard", options =>
            {
                options.Retry.Delay = TimeSpan.Zero;
                options.AttemptTimeout.Timeout = attemptTimeout ?? TimeSpan.FromSeconds(15);
            });
        }

        return services.BuildServiceProvider();
    }
}

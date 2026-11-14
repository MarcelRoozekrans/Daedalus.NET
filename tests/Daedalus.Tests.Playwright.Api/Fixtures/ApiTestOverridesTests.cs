using Daedalus.Api;
using Daedalus.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Daedalus.Tests.Playwright.Api;

/// <summary>What <see cref="ApiTestOverrides"/> leaves of the Api's own registrations.</summary>
[TestFixture]
public sealed class ApiTestOverridesTests
{
    /// <summary>
    ///     Red: remove only <see cref="HealthCheckService"/>, as matching on the type name did; the Api's
    ///     <c>IConfigureOptions&lt;HealthCheckServiceOptions&gt;</c> survives and still registers the Npgsql check.
    /// </summary>
    [Test]
    public void Overrides_leave_no_health_check_of_the_api()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        ApiTestOverrides.ConfigureHost(builder.WebHost, "Host=localhost;Database=unused;Username=u;Password=p");
        ApiApplication.AddServices(builder);

        // The Api registers its database check; without it this test would pass for want of anything to remove.
        using (var before = builder.Services.BuildServiceProvider())
        {
            before.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
                .Should().NotBeEmpty("the Api registers health checks for the overrides to remove");
        }

        ApiTestOverrides.Apply(builder.Services);

        using var after = builder.Services.BuildServiceProvider();
        after.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Select(r => r.Name)
            .Should().BeEmpty("the overrides replace the Api's health checks with plain ones");
        after.GetService<HealthCheckService>().Should().NotBeNull("/health must still be served");
    }
}

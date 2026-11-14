using Daedalus.Api;

var builder = WebApplication.CreateBuilder(args);
var api = ApiApplication.AddServices(builder);

var app = builder.Build();
api.UsePipeline(app);

await app.RunAsync();

namespace Daedalus.Api
{
    /// <summary>
    ///     Partial Program class to make the entry point accessible for WebApplicationFactory in E2E tests.
    /// </summary>
#pragma warning disable S2094 // Classes should not be empty - Required for WebApplicationFactory test access
    public class Program;
#pragma warning restore S2094
}

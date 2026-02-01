using Daedalus.Infrastructure.Persistence;
using Daedalus.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

// Add console logging (minimal for migration tool)
builder.Services.AddConsoleLogging();

// Add database context (key must match the Aspire database resource name)
builder.Services.AddApplicationDatabase(
    builder.Configuration,
    "daedalus",
    DatabaseSettings.GetDefaultConnectionString());

var host = builder.Build();

try
{
    using var scope = host.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    logger.LogInformation("Starting database migration");
    await dbContext.Database.MigrateAsync();
    logger.LogInformation("Database migration completed successfully");
}
catch (Exception ex)
{
    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    logger.LogError(ex, "Database migration failed");
    Environment.Exit(1);
}

Environment.Exit(0);

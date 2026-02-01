using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Threading.RateLimiting;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Daedalus.Application.Extensions;
using Daedalus.Infrastructure.Extensions;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Persistence.Repositories;
using Daedalus.Infrastructure.Services;
using Daedalus.ServiceDefaults;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Asp.Versioning;
using Microsoft.AspNetCore.ResponseCompression;

[assembly:
    UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "ASP.NET Core MVC does not support trimming; IL2026 warnings are expected")]

var builder = WebApplication.CreateBuilder(args);

// Enable enhanced model metadata support early
AppContext.SetSwitch("Microsoft.AspNetCore.Mvc.ApiExplorer.IsEnhancedModelMetadataSupported", true);

// Add service defaults (OpenTelemetry, logging)
builder.Services.AddServiceDefaults();

// Add core infrastructure services (system clock, etc.)
builder.Services.AddCoreInfrastructureServices();

// Add database context
builder.Services.AddApplicationDatabase(builder.Configuration, "daedalus");

// Register RalphLoop configuration using options pattern with manual binding
builder.Services.Configure<RalphLoopConfiguration>(options =>
    builder.Configuration.GetSection(RalphLoopConfiguration.SectionName).Bind(options));

// Add application layer services (command/query handlers, prompt builders)
builder.Services.AddApplicationServices(builder.Configuration);

// Add external service integrations (MCP agents, workspace context)
builder.Services.AddExternalServices(builder.Configuration);

// Add Agent Framework services (Claude via IRalphAgentFactory, MCP tools)
builder.Services.AddAgentFrameworkServices(builder.Configuration);

// Add code analysis services (Ralph Loop orchestration, Git operations)
builder.Services.AddCodeAnalysisServices(builder.Configuration);

// Add repositories
builder.Services.AddScoped<ITaskRepository, TaskRepository>();
builder.Services.AddScoped<IExecutionSessionRepository, ExecutionSessionRepository>();
builder.Services.AddScoped<IProjectRepository, ProjectRepository>();
builder.Services.AddScoped<IRepositoryConfigurationRepository, RepositoryConfigurationRepository>();

// Add API services
builder.Services.AddScoped<ITaskQueryService, TaskQueryService>();
builder.Services.AddScoped<IExecutionSessionQueryService, ExecutionSessionQueryService>();
builder.Services.AddScoped<ITaskExecutionQueryService, TaskExecutionQueryService>();
builder.Services.AddScoped<IProjectQueryService, ProjectQueryService>();

// Add global exception handler (converts unhandled exceptions to RFC 7807 ProblemDetails)
builder.Services.AddExceptionHandler<Daedalus.Api.Middleware.GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// Add API versioning (query-string + header, non-breaking — existing routes keep working)
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
    options.ApiVersionReader = ApiVersionReader.Combine(
        new QueryStringApiVersionReader("api-version"),
        new HeaderApiVersionReader("x-api-version"),
        new UrlSegmentApiVersionReader());
});

// Add controllers with FluentValidation filter for automatic DTO validation
builder.Services.AddControllers(options =>
{
    options.Filters.Add<Daedalus.Api.Middleware.FluentValidationFilter>();
});

// Add response compression (reduces JSON payloads by ~80%)
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<GzipCompressionProvider>();
#pragma warning disable CA1861 // Avoid constant arrays as arguments
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes
        .Concat(new[] { "application/json" });
#pragma warning restore CA1861
});
builder.Services.Configure<GzipCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});

// Add CORS with restricted origins per environment
builder.Services.AddCors(options =>
{
    var allowedOrigins = builder.Configuration
        .GetSection("Cors:AllowedOrigins")
        .Get<string[]>() ?? Array.Empty<string>();

    options.AddPolicy("RestrictedCors", policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials();
        }
        else if (builder.Environment.IsDevelopment())
        {
            // Development: allow localhost on common ports
            policy.AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader();
        }
        else
        {
            // Production: no CORS by default
            policy.WithOrigins();
        }
    });
});

// Add health checks for Aspire orchestration
builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("daedalus") ??
               throw new InvalidOperationException("Database connection string not found"));

// Add JWT Bearer authentication — validates tokens issued by the OIDC provider
var authSection = builder.Configuration.GetSection("Authentication");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authSection["Authority"];
        options.Audience = authSection["Audience"];
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.TokenValidationParameters.ValidateIssuer = true;
        options.TokenValidationParameters.ValidateAudience = true;
    });
// Add RBAC authorization policies
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("TaskRead", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("TaskManagement", policy => policy.RequireRole("task-manager", "admin"));
    options.AddPolicy("ProjectRead", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("ProjectManagement", policy => policy.RequireRole("project-manager", "admin"));
    options.AddPolicy("CodeAnalysis", policy => policy.RequireRole("analyst", "admin"));
    options.AddPolicy("CodeAnalysisRead", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("Admin", policy => policy.RequireRole("admin"));
});

// Add rate limiting — protects write and LLM endpoints from abuse
builder.Services.AddRateLimiter(options =>
{
    // Global: 100 requests per minute per user
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.User?.Identity?.Name
                          ?? context.Connection.RemoteIpAddress?.ToString()
                          ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    // LLM operations: 10 requests per minute (expensive AI calls)
    options.AddFixedWindowLimiter("llm-operations", limiterOptions =>
    {
        limiterOptions.PermitLimit = 10;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        limiterOptions.QueueLimit = 0;
    });

    // Write operations: 30 requests per minute
    options.AddFixedWindowLimiter("write-operations", limiterOptions =>
    {
        limiterOptions.PermitLimit = 30;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        limiterOptions.QueueLimit = 0;
    });

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/problem+json";
        await context.HttpContext.Response.WriteAsync(
            """{"type":"https://tools.ietf.org/html/rfc6585#section-4","title":"Too Many Requests","status":429,"detail":"Rate limit exceeded. Please retry after a short delay."}""",
            cancellationToken);
    };
});

var app = builder.Build();

// Configure middleware
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseExceptionHandler(); // Global exception handler for non-development environments

app.UseResponseCompression(); // Enable Gzip compression for responses
app.UseCors("RestrictedCors");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health"); // Required for Aspire AppHost health checks

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

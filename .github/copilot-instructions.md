# Copilot Instructions - Daedalus .NET Development

## Quick Architecture Overview

**Daedalus** is a high-performance .NET 10 application using Railway-Oriented Programming for task execution with AI/LLM iteration loops. It has **dual presentation layers** sharing one **Application + Infrastructure stack**:

- **Web Layer** (Blazor): REST API → HTTP controllers (Daedalus.Api)
- **Console Layer** (Ralph Loop): Background worker (Daedalus.Console) with direct database access for low-latency polling
- **Shared**: Application/Domain services, EF Core repositories, PostgreSQL persistence

**Key architectural decision**: Console worker bypasses HTTP to minimize latency. Both layers use the same CQRS services/repositories. See [architecture-diagrams.md](docs/architecture-diagrams.md) for 13+ detailed diagrams.

---

## Important: Context7 MCP Usage for Library Documentation

When working with external libraries, **proactively query Context7 MCP** to fetch up-to-date official documentation before implementing code.

**Key Points:**

- Check Context7 **automatically** when adding new libraries or implementing library-specific features
- Use `mcp_context7_resolve-library-id` to find the correct library ID
- Use `mcp_context7_query-docs` to fetch official documentation
- Apply documented patterns to implementation; do NOT rely solely on training data for external libraries

See [`.github/context7-auto-usage.md`](context7-auto-usage.md) for detailed trigger conditions.

---

## Tech Stack

- **Language**: C# 13.0
- **Framework**: .NET 10
- **Pattern Library**: CSharpFunctionalExtensions (Railway-Oriented Programming)
- **Zero-Allocation LINQ**: ZLinq (1.5.4)
- **Database**: PostgreSQL (via Npgsql/EF Core)
- **Testing**: xUnit, NUnit, Playwright (E2E)
- **Code Analysis**: SonarAnalyzer.CSharp, Microsoft.CodeAnalysis.NetAnalyzers, Meziantou.Analyzer
- **Orchestration**: .NET Aspire

---

## Developer Workflows

### Running the Application

```bash
# Start all services (API, Console worker, Database) via .NET Aspire
dotnet run --project src/Daedalus.AppHost

# Dashboard: http://localhost:17300
# Environment variables automatically set via launchSettings.json
```

### Building & Testing

```bash
# Full build
dotnet build

# Run all tests
dotnet test

# Run specific test suite
dotnet test tests/Daedalus.Tests.Unit
dotnet test tests/Daedalus.Tests.Integration
dotnet test tests/Daedalus.Tests.E2E

# Run with code coverage
dotnet test --collect:"XPlat Code Coverage"

# Format code (required before commit)
dotnet format
```

### Console Worker (Ralph Loop) Development

The **Daedalus.Console** project is a background worker that polls tasks and executes them via an LLM iteration loop:

```bash
# Run console worker standalone
dotnet run --project src/Daedalus.Console

# Key components:
# - RalphLoopService.cs: Core iteration loop orchestrator
# - RalphLoopWorker.cs: Hosted service that runs the loop
# - TaskRepository, ExecutionSessionRepository: Direct DB access (no HTTP)
```

**Ralph Loop Pattern**: The worker repeatedly feeds the same prompt to an LLM, checking for completion signals. This enables iterative AI-driven task completion. See [ralph-wiggum-technique.md](docs/ralph-wiggum-technique.md).

### Project Structure

```
src/
├── Domain/                    # Entities (Task, TaskExecution, GitOperation, etc.)
├── Application/               # CQRS handlers, DTOs, services, repository interfaces
├── Infrastructure/            # EF Core DbContext, repository implementations, migrations
├── Api/                       # REST controllers, request/response models
├── Console/                   # Ralph Loop worker, hosted service
├── Web/                       # Blazor WebAssembly UI
└── AppHost/                   # .NET Aspire orchestration

tests/
├── Unit/                      # Domain/Application logic tests
├── Integration/               # Database and service integration tests
└── E2E/                       # Playwright browser tests
```

---

## Critical Architecture Insights

### Dual Presentation Layers, Shared Infrastructure

- **API Layer** (Daedalus.Api) → HTTP REST controllers → good for web clients
- **Console Worker** (Daedalus.Console) → Direct DB, no HTTP → optimal for background task polling
- Both use the **same Application layer** (CQRS, repositories, services)
- Both use the **same Domain layer** (entities, business logic)
- **Why this design?** Console worker needs millisecond-level latency (5-second polling cycles). HTTP overhead would kill performance.

### Task Claiming & Distribution

- Multiple worker instances can run simultaneously
- Each instance claims tasks by setting `CurrentSessionId` in the database (optimistic locking pattern)
- Heartbeat monitoring: Workers send heartbeats every X seconds; stale tasks auto-reclaim after timeout
- See `TaskRepository.ClaimNextTaskAsync()` and `ExecutionSessionRepository` for implementation

### CQRS + Railway-Oriented Programming

- **Commands**: CreateTask, ExecuteTask, UpdateTask, AbandonTask — all return `Result<T>` (success or failure)
- **Queries**: GetAllTasks, GetTaskById — also return `Result<T>`
- Never throw exceptions for expected failures; chain operations with `.Bind()` and `.Map()`
- Error handling is explicit, traceable, and testable

### Configuration Management

- **Application layer** configuration: appsettings.json → IConfiguration
- **Infrastructure layer** configuration: ExternalServicesConfiguration, McpServerConfiguration
- Primary constructor DI: `public class TaskService(IRepository<Task> repo, ILogger<TaskService> logger)`
- No need for private fields — parameters auto-become accessible

### Database Access Patterns

- **API layer**: DbContext scoped per HTTP request
- **Console worker**: DbContext managed by hosted service, shared across polling iterations
- Always use `AsNoTracking()` for read-only queries
- Use `.ExecuteUpdateAsync()`, `.ExecuteDeleteAsync()` for bulk operations instead of loading entities
- Use `DbContextPooling` to reuse DbContext instances (reduces allocation pressure)

---

## Core Principles

### 1. Performance & Allocation Optimization

Always write code that is performant with minimal allocations:

- **Use `Span<T>` and `Memory<T>`** for buffer operations instead of allocating new arrays
- **Use `ArrayPool<T>`** for temporary buffers (especially >= 85KB to avoid LOH)
- **Prefer `stackalloc`** for small, short-lived buffers
- **Use `StringBuilder`** pooling via `ObjectPool<StringBuilder>`
- **Use ZLinq for LINQ operations** - provides zero-allocation LINQ via `AsValueEnumerable()` for hot paths
- **Avoid standard LINQ in hot paths** - use ZLinq or manual loops for performance-critical code
- **Use `ValueTask<T>`** when operations complete synchronously most of the time
- **Prefer `struct` over `class`** for small, short-lived data types
- **Use `readonly struct`** and `in` parameters to avoid defensive copies
- **Use `string.Create`** for complex string building
- **Use `SearchValues<T>`** for character searches (.NET 8+)
- **Avoid `ToString()` in hot paths** - especially at high throughput (>1000 RPS). Every call allocates. Pre-compute or use value-based comparisons instead
- **Avoid string concatenation in hot paths** - use `StringBuilder`, `string.Concat()`, or `string.Create()` depending on complexity
- **Use `LoggerMessageAttribute`** for compile-time logging source generation (.NET 6+):
    - Eliminates allocations for logging metadata at runtime
    - Eliminates boxing of value types in log messages
    - Provides compile-time validation of log messages and parameters
    - Mark classes as `partial` and use `[LoggerMessage]` attributes with static partial methods
    - Each logging method should have a unique EventId
    - Prefer over `ILogger.LogXxx()` extension methods for performance-critical paths

### 2. Async Best Practices

- **Never block on async code** - no `Task.Wait()`, `.Result`, or `.GetAwaiter().GetResult()`
- **Never use `async void`** except for event handlers
- **Use `ConfigureAwait(false)`** in library code
- **Use `CancellationToken`** on all async operations
- **Don't use `Task.Run`** to make synchronous APIs async
- **Stream large datasets with `IAsyncEnumerable<T>`**:
    - Reduces memory pressure by processing data incrementally
    - Improves time-to-first-byte
    - Preferred over materializing full results into memory
    - Controllers returning `IAsyncEnumerable<T>` automatically stream over HTTP/2

### 2b. Null Checking with `as` Operator

- **Use `as` operator for safe type casting** - provides null-safe type checks:

    ```csharp
    // ✅ GOOD - Use as operator for safe null checks on casting
    public void ProcessValue(object value)
    {
        if (value as string is { Length: > 0 } str)
        {
            Console.WriteLine(str);
        }

        var customer = obj as Customer;
        if (customer is not null)
        {
            ProcessCustomer(customer);
        }
    }

    // ❌ BAD - Using direct cast throws on null
    public void ProcessValue(object value)
    {
        var str = (string)value;  // Throws InvalidCastException if null
        if (str != null && str.Length > 0)
        {
            Console.WriteLine(str);
        }
    }
    ```

- **Combine `as` with pattern matching** for elegant null-aware type checking
- **Returns null gracefully** instead of throwing exceptions when type doesn't match

### 3. Railway-Oriented Programming (CSharpFunctionalExtensions)

continue
Always use Result types instead of exceptions for expected failures:

```csharp
// ✅ GOOD - Use Result<T> for operations that can fail
public async Task<Result<Customer>> GetCustomerAsync(Guid id, CancellationToken ct)
{
    var customer = await _dbContext.Customers
        .AsNoTracking()
        .FirstOrDefaultAsync(c => c.Id == id, ct);

    return customer is not null
        ? Result.Success(customer)
        : Result.Failure<Customer>($"Customer with ID {id} not found");
}

// ✅ GOOD - Chain operations with Bind/Map
public async Task<Result<OrderConfirmation>> PlaceOrderAsync(OrderRequest request, CancellationToken ct)
{
    return await ValidateRequest(request)
        .Bind(r => GetCustomerAsync(r.CustomerId, ct))
        .Bind(c => CheckInventoryAsync(request.Items, ct))
        .Map(inventory => CreateOrder(request, inventory))
        .Bind(o => SaveOrderAsync(o, ct))
        .Map(o => new OrderConfirmation(o.Id, o.Total));
}

// ❌ BAD - Throwing exceptions for expected failures
public async Task<Customer> GetCustomerAsync(Guid id)
{
    var customer = await _dbContext.Customers.FindAsync(id);
    if (customer is null)
        throw new CustomerNotFoundException(id); // Don't do this!
    return customer;
}
```

### 4. Entity Framework Core Best Practices

- **Use `AsNoTracking()`** for read-only queries
- **Use `DbContext pooling`** via `AddDbContextPool<T>()`
- **Use compiled queries** for frequently executed queries
- **Use `Split Queries`** for complex includes to avoid cartesian explosion
- **Filter and project at database level** - use `.Where()` and `.Select()` before materializing
- **Avoid N+1 queries** - use eager loading or explicit loading strategically
- **Use `ExecuteUpdateAsync`/`ExecuteDeleteAsync`** for bulk operations
- **Use transactions explicitly** with `BeginTransactionAsync`

### 5. Dependency Injection & Primary Constructors

- **Use `IServiceScopeFactory`** when needing scoped services in background tasks
- **Prefer primary constructors** (C# 12+) for dependency injection - cleaner, less boilerplate
- **Use `Keyed Services`** when multiple implementations exist
- **Register services with appropriate lifetimes**:
    - Singleton: Stateless services, configuration
    - Scoped: DbContext, unit of work
    - Transient: Lightweight, stateless services

#### Primary Constructor Patterns

**Primary constructors eliminate boilerplate and automatically assign parameters to backing fields:**

```csharp
// ✅ GOOD - Primary constructor with DI
public class CustomerService(ICustomerRepository repository, ILogger<CustomerService> logger)
    : ICustomerService
{
    // repository and logger are automatically available as fields
    public async Task<Result<Customer>> GetCustomerAsync(Guid id, CancellationToken ct)
    {
        var customer = await repository.GetByIdAsync(id, ct);
        return customer is not null
            ? Result.Success(customer)
            : Result.Failure<Customer>($"Customer {id} not found");
    }
}

// ✅ GOOD - Primary constructor with value objects
public readonly struct Email(string value)
{
    public string Value => value;

    public static Result<Email> Create(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Result.Failure<Email>("Email cannot be empty");

        return Result.Success(new Email(input.Trim().ToLowerInvariant()));
    }
}

// ✅ GOOD - Primary constructor with records
public record CreateCustomerCommand(string Name, string Email, string Phone);

public class CreateCustomerCommandHandler(IRepository<Customer> repository)
    : ICommandHandler<CreateCustomerCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateCustomerCommand command, CancellationToken ct)
    {
        var emailResult = Email.Create(command.Email);
        if (emailResult.IsFailure)
            return Result.Failure<Guid>(emailResult.Error);

        var customer = new Customer(command.Name, emailResult.Value, command.Phone);
        var result = await repository.AddAsync(customer, ct);

        return result.IsSuccess
            ? Result.Success(customer.Id)
            : Result.Failure<Guid>(result.Error);
    }
}

// ✅ GOOD - Primary constructor in controllers
[ApiController]
[Route("api/[controller]")]
public class CustomersController(ICustomerService service) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetCustomer(Guid id, CancellationToken ct)
    {
        var result = await service.GetCustomerAsync(id, ct);
        return result.Match<IActionResult>(
            customer => Ok(customer),
            error => NotFound()
        );
    }
}

// ❌ BAD - Traditional boilerplate
public class CustomerService : ICustomerService
{
    private readonly ICustomerRepository _repository;
    private readonly ILogger<CustomerService> _logger;

    public CustomerService(ICustomerRepository repository, ILogger<CustomerService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<Result<Customer>> GetCustomerAsync(Guid id, CancellationToken ct)
    {
        var customer = await _repository.GetByIdAsync(id, ct);
        return customer is not null
            ? Result.Success(customer)
            : Result.Failure<Customer>($"Customer {id} not found");
    }
}

// ❌ BAD - Mixing primary and traditional constructors
public class CustomerService(ICustomerRepository repository)
{
    private readonly ILogger<CustomerService> _logger; // Inconsistent!

    public CustomerService(ICustomerRepository repository, ILogger<CustomerService> logger)
        : this(repository) // Creating multiple constructors defeats the purpose
    {
        _logger = logger;
    }
}
```

**Primary Constructor Benefits:**

- Reduces boilerplate - no need for private fields + traditional constructor
- Automatic parameter capture - parameters are automatically available in the class
- Works with inheritance (C# 12.1+) - primary constructor parameters can be passed to base
- Type-safe - compiler ensures all parameters are properly captured
- Minimal allocations - no extra field assignments

### 6. API Design

```csharp
// ✅ GOOD - Async controller with proper response types
[HttpGet("{id:guid}")]
[ProducesResponseType<CustomerDto>(StatusCodes.Status200OK)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
public async Task<IActionResult> GetCustomer(Guid id, CancellationToken ct)
{
    var result = await _customerService.GetCustomerAsync(id, ct);

    return result.Match<IActionResult>(
        customer => Ok(customer),
        error => NotFound(new ProblemDetails { Detail = error })
    );
}

// ✅ GOOD - Use TypedResults for minimal APIs
app.MapGet("/customers/{id:guid}", async (Guid id, ICustomerService service, CancellationToken ct) =>
{
    var result = await service.GetCustomerAsync(id, ct);
    return result.Match(
        customer => Results.Ok(customer),
        error => Results.NotFound()
    );
});
```

### 6. API Performance Optimization

**Response Compression:**

- Enable Gzip compression to reduce JSON payloads by ~80% (45KB → 8KB)
- Enable for HTTPS and configure fastest compression level for APIs:

```csharp
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes
        .Concat(new[] { "application/json" });
});
builder.Services.Configure<GzipCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});
app.UseResponseCompression();
```

**JSON Serialization Optimization:**

- Use `System.Text.Json` source generators (`JsonSerializerContext`) for:
    - Reduced allocations during serialization
    - Compile-time validation of serialization contracts
    - No reflection overhead at runtime

```csharp
[JsonSerializable(typeof(CustomerDto))]
internal partial class ApiJsonContext : JsonSerializerContext { }

// Configure default options
services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});
```

**Caching Strategies:**

- **Response Caching** for idempotent endpoints:

```csharp
[HttpGet]
[ResponseCache(Duration = 60)]
public async Task<IActionResult> GetCustomer(Guid id, CancellationToken ct)
{
    var result = await _service.GetCustomerAsync(id, ct);
    return result.Match<IActionResult>(
        customer => Ok(customer),
        error => NotFound()
    );
}
```

- **In-Memory Caching** via `IMemoryCache` for expensive operations
- **Distributed Caching** for multi-instance deployments via `IDistributedCache`
- Always set `AbsoluteExpirationRelativeToNow` or `SlidingExpiration` to prevent stale data

### 7. HttpContext Safety

- **Never store `HttpContext` in a field** - always access via `IHttpContextAccessor.HttpContext`
- **Never access `HttpContext` from multiple threads**
- **Copy required data before background tasks**
- **Check `Response.HasStarted`** before modifying headers

### 8. Resilient HTTP Calls with Polly

**Use Polly to make HTTP calls resilient with retry, circuit breaker, and timeout policies:**

```csharp
// ✅ GOOD - Configure resilient HTTP client with Polly
public static void AddResilientHttpClient(this IServiceCollection services)
{
    var retryPolicy = HttpPolicyExtensions
        .HandleTransientHttpError()
        .OrResult(r => r.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        .WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: retryAttempt =>
                TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
            onRetry: (outcome, timespan, retryCount, context) =>
            {
                // Log retry attempts
            }
        );

    var circuitBreakerPolicy = HttpPolicyExtensions
        .HandleTransientHttpError()
        .OrResult(r => r.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        .CircuitBreakerAsync(
            handledEventsAllowedBeforeBreaking: 5,
            durationOfBreak: TimeSpan.FromSeconds(30),
            onBreak: (outcome, timespan, context) =>
            {
                // Log circuit breaker triggered
            }
        );

    var timeoutPolicy = Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(10));

    services.AddHttpClient<IExternalApiClient, ExternalApiClient>()
        .AddPolicyHandler(retryPolicy)
        .AddPolicyHandler(circuitBreakerPolicy)
        .AddPolicyHandler(timeoutPolicy);
}

// ✅ GOOD - Use configured HttpClient in services
public class ExternalApiClient(HttpClient httpClient) : IExternalApiClient
{
    public async Task<Result<ApiResponse>> GetDataAsync(string endpoint, CancellationToken ct)
    {
        try
        {
            var response = await httpClient.GetAsync(endpoint, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return Result.Success(JsonSerializer.Deserialize<ApiResponse>(json)!);
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<ApiResponse>($"HTTP request failed: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            return Result.Failure<ApiResponse>("Request timeout");
        }
    }
}

// ❌ BAD - No resilience policies
var response = await _httpClient.GetAsync(url);
var data = await response.Content.ReadAsAsync<ApiResponse>();
```

**Polly Best Practices:**

- **Combine retry + circuit breaker** - retry for transient failures, circuit breaker to fail fast when service is down
- **Use exponential backoff** - prevent overwhelming struggling services (2^attempt seconds)
- **Set appropriate timeouts** - always set a timeout policy to prevent hanging requests
- **Log policy events** - track retries, circuit breaker trips, timeouts for observability
- **Use `AddPolicyHandler` order correctly** - timeouts should be innermost (executed first), circuit breaker outer
- **Configure appropriate thresholds** - adjust retry count, circuit breaker break duration based on SLA requirements
- **Use `PolicyWrap`** for complex scenarios requiring multiple stacked policies

---

## Code Patterns

### Value Objects

```csharp
public readonly record struct Email
{
    public string Value { get; }

    private Email(string value) => Value = value;

    public static Result<Email> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result.Failure<Email>("Email cannot be empty");

        if (!value.Contains('@'))
            return Result.Failure<Email>("Email must contain @");

        return Result.Success(new Email(value.Trim().ToLowerInvariant()));
    }

    public override string ToString() => Value;
    public static implicit operator string(Email email) => email.Value;
}
```

### Repository Pattern with Result

```csharp
public interface IRepository<T> where T : class
{
    Task<Result<T>> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Result<IReadOnlyList<T>>> GetAllAsync(CancellationToken ct);
    Task<Result<T>> AddAsync(T entity, CancellationToken ct);
    Task<Result> UpdateAsync(T entity, CancellationToken ct);
    Task<Result> DeleteAsync(Guid id, CancellationToken ct);
}
```

### Specification Pattern

```csharp
public abstract class Specification<T>
{
    public abstract Expression<Func<T, bool>> ToExpression();

    public bool IsSatisfiedBy(T entity)
    {
        var predicate = ToExpression().Compile();
        return predicate(entity);
    }
}

public static class SpecificationExtensions
{
    public static IQueryable<T> Where<T>(this IQueryable<T> query, Specification<T> spec)
        => query.Where(spec.ToExpression());
}
```

### Clean Architecture Testing with ArchUnitNET

**Use ArchUnitNET to enforce clean architecture rules and validate layer dependencies:**

```csharp
using ArchUnitNET.Fluent;
using ArchUnitNET.Xunit;

public class ArchitectureTests
{
    private static readonly Architecture Architecture = new ArchitectureBuilder()
        .WithoutErrors()
        .Build();

    private static readonly IObjectProvider<IType> DomainTypes =
        ArchRuleList.GetClassesInNamespace("Daedalus.Domain");

    private static readonly IObjectProvider<IType> ApplicationTypes =
        ArchRuleList.GetClassesInNamespace("Daedalus.Application");

    private static readonly IObjectProvider<IType> InfrastructureTypes =
        ArchRuleList.GetClassesInNamespace("Daedalus.Infrastructure");

    private static readonly IObjectProvider<IType> ApiTypes =
        ArchRuleList.GetClassesInNamespace("Daedalus.Api");

    // ✅ GOOD - Domain layer should not depend on other layers
    [Fact]
    public void DomainShouldNotDependOnOtherLayers()
    {
        var rule = Types()
            .That()
            .Are(DomainTypes)
            .Should()
            .NotDependOnAny(ApplicationTypes, InfrastructureTypes, ApiTypes)
            .Because("Domain layer should be independent and not depend on higher layers");

        rule.Check(Architecture);
    }

    // ✅ GOOD - Application should not depend on Infrastructure or API
    [Fact]
    public void ApplicationShouldNotDependOnInfrastructureOrApi()
    {
        var rule = Types()
            .That()
            .Are(ApplicationTypes)
            .Should()
            .NotDependOnAny(InfrastructureTypes, ApiTypes)
            .Because("Application layer should only depend on Domain, not Infrastructure or API");

        rule.Check(Architecture);
    }

    // ✅ GOOD - Entities should be in Domain layer only
    [Fact]
    public void EntitiesShouldBeInDomainLayer()
    {
        var rule = Classes()
            .That()
            .ResideInNamespace("Daedalus.Domain.Entities")
            .Should()
            .NotBePublic()
            .Or()
            .BePublic()
            .Because("Entities are core domain concepts");

        rule.Check(Architecture);
    }

    // ✅ GOOD - Repositories should be defined in Application, implemented in Infrastructure
    [Fact]
    public void RepositoryInterfacesShouldBeInApplication()
    {
        var rule = Interfaces()
            .That()
            .HaveName("IRepository*")
            .Should()
            .ResideInNamespace("Daedalus.Application")
            .Because("Repository contracts belong in Application layer");

        rule.Check(Architecture);
    }

    // ✅ GOOD - Controllers should be in API layer
    [Fact]
    public void ControllersShouldBeInApiLayer()
    {
        var rule = Classes()
            .That()
            .HaveName("*Controller")
            .Should()
            .ResideInNamespace("Daedalus.Api")
            .Because("Controllers are API concerns");

        rule.Check(Architecture);
    }

    // ✅ GOOD - No circular dependencies
    [Fact]
    public void NoCircularDependencies()
    {
        var rule = SliceRules()
            .SlicesMatching("Daedalus.(*).")
            .Should()
            .NotDependOnEachOther()
            .Because("Circular dependencies are anti-patterns");

        rule.Check(Architecture);
    }
}
```

**ArchUnitNET Best Practices:**

- **Define clear namespace rules** - map architecture layers to namespaces (Domain, Application, Infrastructure, Api)
- **Test dependency direction** - verify lower layers don't depend on higher layers
- **Enforce naming conventions** - ensure classes follow naming patterns (Controllers, Entities, etc.)
- **Validate layer isolation** - prevent accidental cross-layer dependencies
- **Test circular dependencies** - detect architectural violations automatically
- **Run tests frequently** - add to CI/CD pipeline to catch architecture violations early
- **Keep rules clear and documented** - use `Because()` to explain architectural constraints
- **Refactor rule violations** - treat architecture test failures like any other test failure

**Project Setup:**

```xml
<!-- Add to test project .csproj -->
<ItemGroup>
    <PackageReference Include="ArchUnitNET.xUnit" Version="0.12.1" />
</ItemGroup>
```

### Compile-Time Logging with LoggerMessageAttribute

```csharp
// ✅ GOOD - Use LoggerMessageAttribute for high-performance logging
public partial class CustomerService
{
    private readonly ILogger<CustomerService> _logger;

    public async Task<Result<Customer>> GetCustomerAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var customer = await _dbContext.Customers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == id, ct);

            return customer is not null
                ? Result.Success(customer)
                : Result.Failure<Customer>($"Customer {id} not found");
        }
        catch (Exception ex)
        {
            LogErrorRetrievingCustomer(_logger, ex, id);
            return Result.Failure<Customer>($"Error retrieving customer: {ex.Message}");
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Customer {CustomerId} retrieved successfully")]
    private static partial void LogCustomerRetrieved(ILogger logger, Guid customerId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Error retrieving customer {CustomerId}")]
    private static partial void LogErrorRetrievingCustomer(ILogger logger, Exception exception, Guid customerId);
}

// ❌ BAD - Using extension methods allocates metadata at runtime
public class CustomerService
{
    private readonly ILogger<CustomerService> _logger;

    public async Task<Customer> GetCustomerAsync(Guid id)
    {
        try
        {
            var customer = await _dbContext.Customers.FindAsync(id);
            if (customer is null)
                throw new NotFoundException("Customer not found");

            _logger.LogInformation("Customer {CustomerId} retrieved", id); // Allocates at runtime
            return customer;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving customer {CustomerId}", id); // Allocates at runtime
            throw;
        }
    }
}
```

### Zero-Allocation String Validation with PerformanceOptimizations

The `PerformanceOptimizations` utility class (in `Application.Services`) provides zero-allocation helpers for hot paths:

```csharp
// ✅ GOOD - Use PerformanceOptimizations for validation + trimming
public class CreateTaskCommandHandler : ICommandHandler<CreateTaskCommand, Result<TaskDto>>
{
    public async Task<Result<TaskDto>> Handle(CreateTaskCommand command, CancellationToken ct)
    {
        // Zero-allocation validation: only trims if needed, returns original string if already trimmed
        var prompt = PerformanceOptimizations.ValidateAndTrimString(command.Prompt, out var promptError);
        if (prompt == null)
        {
            return Result.Failure<TaskDto>($"Prompt: {promptError}");
        }

        var promise = PerformanceOptimizations.ValidateAndTrimString(
            command.CompletionPromise, out var promiseError);
        if (promise == null)
        {
            return Result.Failure<TaskDto>($"CompletionPromise: {promiseError}");
        }

        // Use validated strings in domain entity creation
        var createResult = Task.Create(/* ... */, prompt, promise, /* ... */);
        // ...
    }
}

// Available PerformanceOptimizations methods:
// - ValidateAndTrimString(string?, out string?): string? → Zero-allocation validation
// - ContainsTarget(ReadOnlySpan<char>, ReadOnlySpan<char>): bool → Efficient substring search
// - CountOccurrences(ReadOnlySpan<char>, char): int → Zero-allocation character counting
// - CreateOptimizedBuilder(int): StringBuilder → Pre-allocated StringBuilder with 10% buffer

// ❌ BAD - Unnecessary allocations in validation
if (string.IsNullOrWhiteSpace(command.Prompt))
    return Result.Failure<TaskDto>("Prompt cannot be empty");

var prompt = command.Prompt.Trim(); // Always allocates even if already trimmed!
```

### Zero-Allocation LINQ with ZLinq

ZLinq provides zero-allocation LINQ operations for performance-critical paths:

```csharp
using ZLinq;

// ✅ GOOD - Zero-allocation LINQ in hot paths
public async Task<Result<IReadOnlyList<TaskDto>>> GetPendingTasksAsync(CancellationToken ct)
{
    var tasks = await _dbContext.Tasks
        .AsNoTracking()
        .ToListAsync(ct);

    // Use AsValueEnumerable() for zero-allocation filtering/mapping
    var filtered = tasks
        .AsValueEnumerable()
        .Where(t => t.Status == TaskStatus.Pending)
        .Select(t => MapToDto(t))
        .ToList();

    return Result.Success(filtered.AsReadOnly());
}

// ✅ GOOD - Efficient iteration without allocations
public void ProcessActiveTasks(IEnumerable<Task> tasks)
{
    // AsValueEnumerable() works with arrays and List<T> for zero allocations
    foreach (var task in tasks.AsValueEnumerable())
    {
        if (task.IsActive)
            ExecuteTask(task);
    }
}

// ✅ GOOD - Chain multiple LINQ operations with ZLinq
var results = data
    .AsValueEnumerable()
    .Where(x => x.IsActive)
    .Select(x => x.Transform())
    .Where(x => x.Priority > 0)
    .ToList(); // Only allocates the final list

// ❌ BAD - Standard LINQ allocates enumerator for each operation
var results = data
    .Where(x => x.IsActive)      // Allocates enumerator
    .Select(x => x.Transform())  // Allocates enumerator
    .ToList();                   // Allocates again
```

**Key ZLinq Patterns:**

- `AsValueEnumerable()` - Convert arrays/Lists to zero-allocation iterables
- Works with `Span<T>` in .NET 9+ for stack-based iteration
- Only allocates when calling `.ToList()` or materializing results
- Drop-in replacement for standard LINQ in hot paths

### Advanced Struct Patterns for Zero Allocations

**Use `readonly struct` to Avoid Defensive Copies:**

- Mark immutable structs as `readonly` so compiler knows they cannot be modified
- Prevents defensive copying when passed as `in` parameters
- Use `readonly` on methods/properties that don't mutate state to avoid copies

```csharp
// ✅ GOOD - Immutable struct, compiler avoids defensive copies
public readonly struct Point3D
{
    public double X { get; }
    public double Y { get; }
    public double Z { get; }

    public Point3D(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    // Mark methods that don't modify state as readonly
    public readonly double DistanceFromOrigin() =>
        Math.Sqrt(X * X + Y * Y + Z * Z);
}

// ✅ GOOD - Use in parameters for large structs (>IntPtr.Size)
private static double CalculateDistance(in Point3D p1, in Point3D p2)
{
    var xDiff = p1.X - p2.X;
    var yDiff = p1.Y - p2.Y;
    var zDiff = p1.Z - p2.Z;
    return Math.Sqrt(xDiff * xDiff + yDiff * yDiff + zDiff * zDiff);
}

// ❌ BAD - Mutable struct with in parameter causes defensive copies
public struct MutablePoint3D
{
    public double X { get; set; }  // Mutable property
    // ... compiler creates temp copies when accessed!
}
```

**Use `ref readonly return` for Large Structs:**

- Return large structs by reference when storage lifetime exceeds method scope
- Avoids copying while maintaining immutability guarantees

```csharp
private static Point3D _origin = new Point3D(0, 0, 0);

// ✅ GOOD - Return by reference to avoid copying
public static ref readonly Point3D GetOrigin() => ref _origin;

// Usage
var origin = Point3D.GetOrigin();           // Copy if needed
ref readonly var originRef = ref Point3D.GetOrigin();  // Reference
```

**Use `Memory<T>` and `MemoryPool<T>` for Heap Allocations:**

- `Span<T>` is stack-allocated (ref struct), cannot cross async boundaries
- `Memory<T>` is heap-allocated, can be stored in fields and passed across async boundaries
- `MemoryPool<T>` for renting memory blocks instead of allocating new ones

```csharp
// ✅ GOOD - Use MemoryPool for temporary allocations
public async Task ProcessDataAsync(int size, CancellationToken ct)
{
    using (var handle = MemoryPool<byte>.Shared.Rent(size))
    {
        var memory = handle.Memory;
        // Use memory...
    } // Auto-disposes when done
}

// ✅ GOOD - Use Memory<T> in class fields and async methods
public class DataProcessor
{
    private readonly IMemoryOwner<byte> _buffer;

    public DataProcessor(int bufferSize)
    {
        _buffer = MemoryPool<byte>.Shared.Rent(bufferSize);
    }

    public async Task ProcessAsync(CancellationToken ct)
    {
        // Can use Memory across await boundaries
        await WriteToStreamAsync(_buffer.Memory, ct);
    }

    public void Dispose() => _buffer.Dispose();
}
```

### Performance Measurement & Profiling

When optimizing for zero-allocation patterns:

- **Always measure before and after** - Use benchmarks (BenchmarkDotNet in `benchmarks/Daedalus.Benchmarks/`) to validate improvements
- **Focus on GC pressure** - Reducing Gen 0 collections is often more important than reducing absolute allocation counts
- **Profile at representative throughput** - High-throughput scenarios (>1000 RPS) expose allocation issues that low-throughput code hides
- **Monitor latency percentiles** - P95/P99 latency improvements are more meaningful than average latency in production
- **Test with realistic data sizes** - Optimization strategies differ for small vs. large datasets
- **Consider LOH (Large Object Heap) pressure** - Allocations >= 85KB go directly to LOH and cause full GCs
- **Use profiling tools**:
    - **BenchmarkDotNet** - Isolated performance testing with allocation metrics
    - **Visual Studio Profiler** or **JetBrains dotTrace** - Real-time profiling
    - **OpenTelemetry**, **Application Insights**, or **Prometheus** - Production monitoring

## Forbidden Patterns

❌ **Never do these:**

```csharp
// ❌ Blocking on async
var result = GetDataAsync().Result;
var result2 = GetDataAsync().GetAwaiter().GetResult();
Task.WaitAll(tasks);

// ❌ async void (except event handlers)
public async void ProcessData() { }

// ❌ Throwing exceptions for flow control
throw new NotFoundException("Customer not found");

// ❌ Using Task.Run to fake async
public Task<Data> GetDataAsync() => Task.Run(() => GetData());

// ❌ Capturing HttpContext in closures
_ = Task.Run(async () => {
    var path = HttpContext.Request.Path; // BAD!
});

// ❌ Allocating large buffers directly
var buffer = new byte[100_000]; // Use ArrayPool instead

// ❌ String concatenation in loops
foreach (var item in items)
    result += item.ToString(); // Use StringBuilder

// ❌ Synchronous I/O
var json = new StreamReader(Request.Body).ReadToEnd(); // Use async!

// ❌ LINQ in hot paths without consideration
items.Where(x => x.IsActive).ToList(); // Consider manual loop

// ❌ Unused using statements
using System.Collections.Concurrent; // Remove if not used
using System.Net.Http.Json;           // Remove if not used

// ❌ Traditional constructors (use primary constructors instead)
public class CustomerService
{
    private readonly ICustomerRepository _repository;
    private readonly ILogger<CustomerService> _logger;

    public CustomerService(ICustomerRepository repository, ILogger<CustomerService> logger)
    {
        _repository = repository;
        _logger = logger;
    }
}

// ✅ GOOD - Use primary constructors
public class CustomerService(ICustomerRepository repository, ILogger<CustomerService> logger)
{
    // repository and logger are automatically available
}

// ❌ Passing mutable structs as in parameters
public struct MutablePoint { public double X { get; set; } }
private static void Process(in MutablePoint p) { } // Compiler creates defensive copies!

// ❌ Using Nullable<T> with in parameters
private static void Process(in int? value) { } // Nullable<T> isn't readonly, causes copies

// ❌ Storing HttpContext in fields
public class MyService
{
    private readonly HttpContext _context; // NEVER! Context is request-scoped
}

// ✅ GOOD - Access HttpContext via IHttpContextAccessor
public class MyService(IHttpContextAccessor contextAccessor)
{
    public void DoWork()
    {
        var context = contextAccessor.HttpContext;
        // Use context...
    }
}
```

---

## File Naming Conventions

- **Classes**: `PascalCase.cs` (e.g., `CustomerService.cs`)
- **Interfaces**: `IPascalCase.cs` (e.g., `ICustomerService.cs`)
- **DTOs**: `PascalCaseDto.cs` (e.g., `CustomerDto.cs`)
- **Records**: `PascalCaseRecord.cs` or just `PascalCase.cs`
- **Tests**: `ClassNameTests.cs` (e.g., `CustomerServiceTests.cs`)
- **Configuration**: `appsettings.json`, `appsettings.{Environment}.json`

## Code Formatting

- **Always use `dotnet format`** to format code changes:
    ```bash
    dotnet format
    ```
- Runs before committing to ensure consistent style across the codebase
- **Follow `.editorconfig` settings** - this file defines all code style rules for the project:
    - EditorConfig is automatically applied by Visual Studio, VS Code, and `dotnet format`
    - Ensures consistent formatting across all developers and CI/CD pipelines
    - Rules include indentation, naming conventions, spacing, and code analysis settings
    - Never override `.editorconfig` rules in individual editor settings
    - Review `.editorconfig` when starting work to understand project conventions
    - When writing new code, adhere to the style rules defined in `.editorconfig`
- Follow formatting conventions for:
    - Indentation: 4 spaces (defined in `.editorconfig`)
    - Line length: Reasonable limits enforced by analyzer (see `.editorconfig`)
    - Naming: Follow conventions above (enforced by `.editorconfig`)
    - Spacing: Standard C# conventions (defined in `.editorconfig`)

---

## Project Structure

```
src/
├── Domain/                    # Entities, Value Objects, Domain Events
├── Application/               # Use Cases, DTOs, Interfaces
├── Infrastructure/            # EF Core, External Services
├── Api/                       # Controllers, Endpoints, Middleware
└── Shared/                    # Cross-cutting concerns

tests/
├── Unit/                      # Unit tests
├── Integration/               # Integration tests
└── E2E/                       # End-to-end tests
```

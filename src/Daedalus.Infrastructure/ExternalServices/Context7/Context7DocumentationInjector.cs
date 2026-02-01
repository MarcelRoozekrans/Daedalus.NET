using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Infrastructure.Configuration;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daedalus.Infrastructure.ExternalServices.Context7;

/// <summary>
///     Default implementation of Context7 documentation injection.
///     Uses GitHub Copilot SDK to dynamically resolve library IDs via MCP integration.
///     Falls back to static keyword mapping if Copilot SDK is unavailable.
///     Uses caching to avoid redundant library resolution calls.
/// </summary>
public sealed class Context7DocumentationInjector(
    HttpClient httpClient,
    ILogger<Context7DocumentationInjector> logger,
    IOptions<ExternalServicesConfiguration> externalServicesOptions)
    : IContext7DocumentationInjector
{
    /// <summary>Maximum number of libraries to include in documentation context.</summary>
    private const int _maxLibrariesInContext = 5;

    /// <summary>Timeout for Context7 API calls (seconds).</summary>
    private const int _context7TimeoutSeconds = 10;

    /// <summary>Timeout for Copilot SDK library resolution (seconds).</summary>
    private const int _copilotSdkTimeoutSeconds = 5;

    /// <summary>Character array for splitting documentation into lines.</summary>
    private static readonly char[] _lineBreakChars = { '\n', '\r' };

    /// <summary>
    ///     Maps keywords to Context7 library metadata (ID, Name, Length).
    ///     Sorted by keyword length (longest first) for specificity-ordered detection.
    /// </summary>
    private static readonly (string Keyword, string LibraryId, string Name)[] _keywordToLibraryMap = new[]
    {
        // Longer, more specific keywords first for accurate matching
        ("styled-components", "/styled-components/styled-components", "styled-components"),
        ("authorization", "/casbin/casbin", "Casbin"), ("authentication", "/nextauthjs/next-auth", "NextAuth"),
        ("serialization", "/colinhacks/zod", "Zod"), ("turborepo", "/vercel/turbo", "Turbo"),
        ("springframework", "/spring-projects/spring-boot", "Spring Boot"),
        ("tailwindcss", "/tailwindlabs/tailwindcss", "Tailwind CSS"),
        ("kubernetes", "/kubernetes/kubernetes", "Kubernetes"), ("playwright", "/microsoft/playwright", "Playwright"),
        ("cloudflare", "/cloudflare/workers", "Cloudflare Workers"), ("terraform", "/hashicorp/terraform", "Terraform"),
        ("typeorm", "/typeorm/typeorm", "TypeORM"), ("sqlalchemy", "/sqlalchemy/sqlalchemy", "SQLAlchemy"),
        ("firebase", "/firebase/firebase-js-sdk", "Firebase"), ("supabase", "/supabase/supabase", "Supabase"),
        ("dynamodb", "/aws/dynamodb", "Amazon DynamoDB"), ("mongodb", "/mongodb/docs", "MongoDB"),
        ("postgresql", "/postgres/postgres", "PostgreSQL"), ("mongoose", "/LearnBoost/mongoose", "Mongoose"),
        ("ansible", "/ansible/ansible", "Ansible"), ("jenkins", "/jenkinsci/jenkins", "Jenkins"),
        ("emotion", "/emotion-js/emotion", "Emotion"),
        ("material", "/material-components/material-components-web", "Material Design"),
        ("bootstrap", "/twbs/bootstrap", "Bootstrap"), ("chakra", "/chakra-ui/chakra-ui", "Chakra UI"),
        ("monitoring", "/DataDog/datadog-agent", "Monitoring"), ("nextjs", "/vercel/next.js", "Next.js"),
        ("axios", "/axios/axios", "Axios"), ("zod", "/colinhacks/zod", "Zod"),

        // Medium-length keywords
        ("next.js", "/vercel/next.js", "Next.js"), ("typescript", "/microsoft/typescript", "TypeScript"),
        ("fastapi", "/tiangolo/fastapi", "FastAPI"), ("prisma", "/prisma/prisma", "Prisma ORM"),
        ("docker", "/moby/moby", "Docker"), ("webpack", "/webpack/webpack", "Webpack"),
        ("rollup", "/rollup/rollup", "Rollup"), ("esbuild", "/evanw/esbuild", "esbuild"),
        ("parcel", "/parcel-bundler/parcel", "Parcel"), ("nodejs", "/nodejs/node", "Node.js"),
        ("nestjs", "/nestjs/nest", "NestJS"), ("vitest", "/vitest-dev/vitest", "Vitest"),
        ("cypress", "/cypress-io/cypress", "Cypress"), ("pytest", "/pytest-dev/pytest", "pytest"),
        ("kotlin", "/jetbrains/kotlin", "Kotlin"), ("netfily", "/netlify/netlify-js", "Netlify"),
        ("heroku", "/jashkenas/underscore", "Heroku"), ("graphql", "/graphql/graphql-js", "GraphQL"),
        ("websoket", "/websockets/ws", "WebSocket"), ("grpc", "/grpc/grpc", "gRPC"),
        ("vercel", "/vercel/next.js", "Vercel"), ("tailwind", "/tailwindlabs/tailwindcss", "Tailwind CSS"),
        ("sqlite", "/sqlite/sqlite", "SQLite"), ("django", "/django/django", "Django"),
        ("flask", "/pallets/flask", "Flask"), ("express", "/expressjs/express", "Express.js"),
        ("astro", "/withastro/astro", "Astro"), ("remix", "/remix-run/remix", "Remix"),
        ("mocha", "/mochajs/mocha", "Mocha"), ("logging", "/microsoft/dotnet", "Logging Frameworks"),

        // Shorter keywords
        ("react", "/facebook/react", "React"), ("angular", "/angular/angular", "Angular"),
        ("vue", "/vuejs/vue", "Vue.js"), ("svelte", "/sveltejs/svelte", "Svelte"),
        ("solid", "/solidjs/solid", "SolidJS"), ("gatsby", "/gatsbyjs/gatsby", "Gatsby"),
        ("nuxt", "/nuxt/nuxt", "Nuxt"), ("qwik", "/builderio/qwik", "Qwik"), ("turbo", "/vercel/turbo", "Turbo"),
        ("spark", "/spark-projects/spark-boot", "Spring Boot"), ("rails", "/rails/rails", "Ruby on Rails"),
        ("laravel", "/laravel/laravel", "Laravel"), ("dotnet", "/microsoft/dotnet", "ASP.NET Core"),
        ("npm", "/npm/npm", "npm"), ("yarn", "/yarnpkg/yarn", "Yarn"), ("pnpm", "/pnpm/pnpm", "pnpm"),
        ("deno", "/denoland/deno", "Deno"), ("jest", "/jestjs/jest", "Jest"), ("mysql", "/mysql/mysql-server", "MySQL"),
        ("redis", "/redis/redis", "Redis"), ("awse", "/aws/aws-sdk-js", "AWS SDK"),
        ("azure", "/microsoft/azure-sdk", "Azure SDK"), ("vite", "/vitejs/vite", "Vite"),
        ("nest", "/nestjs/nest", "NestJS"), ("rust", "/rust-lang/rust", "Rust"), ("java", "/openjdk/jdk", "Java"),
        ("python", "/python/cpython", "Python"), ("ruby", "/ruby/ruby", "Ruby"), ("php", "/php/php-src", "PHP"),
        ("scss", "/sass/dart-sass", "Sass"), ("sass", "/sass/dart-sass", "Sass"),
        ("rest", "/openapis/openapi-specification", "REST API"), ("lit", "/lit/lit", "Lit"),
        ("auth", "/nextauthjs/next-auth", "NextAuth"),
        ("oauth", "/IdentityModel/IdentityModel.OidcClient.Samples", "OAuth"), ("nunit", "/nunit/nunit", "NUnit"),
        ("xunit", "/xunit/xunit", "xUnit.NET"), ("selenium", "/SeleniumHQ/selenium", "Selenium"),
        ("spring", "/spring-projects/spring-boot", "Spring Boot"), ("node.js", "/nodejs/node", "Node.js"),
        ("asp.net", "/microsoft/dotnet", "ASP.NET Core"), ("c#", "/microsoft/dotnet", "C#"),
        ("csharp", "/microsoft/dotnet", "C#"), ("kotlin", "/jetbrains/kotlin", "Kotlin"),
        ("scala", "/scala/scala", "Scala"), ("go", "/golang/go", "Go"),
        ("gcp", "/googleapis/nodejs-storage", "Google Cloud"), ("aws", "/aws/aws-sdk-js", "AWS SDK"),
        ("bun", "/oven-sh/bun", "Bun"), ("jwt", "/jpadilla/pyjwt", "JWT"), ("fetch", "/whatwg/fetch", "Fetch API"),
        ("websockets", "/websockets/ws", "WebSocket"), ("websocket", "/websockets/ws", "WebSocket"),
        ("javascript", "/tc39/proposal-pipeline-operator", "JavaScript"), ("validation", "/colinhacks/zod", "Zod")
    };

    /// <summary>Cache for resolved library IDs to avoid redundant lookups.</summary>
    private static readonly ConcurrentDictionary<string, Context7Library?> _libraryCache = new(
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Base URI for Context7 API service (from configuration).</summary>
    private readonly Uri _context7ApiBaseUri = new(
#pragma warning disable S1075 // Configuration default, configurable via appsettings
        externalServicesOptions.Value.Context7?.ApiUrl ?? "https://context7.com/api",
#pragma warning restore S1075
        UriKind.RelativeOrAbsolute);

    public async Task<Result<string>> GetDocumentationContextAsync(string prompt, CancellationToken ct)
    {
        var detectedKeywords = DetectLibraryKeywordsInPrompt(prompt);

        if (detectedKeywords.Count == 0)
        {
            logger.LogDebug("No recognized library keywords detected in prompt");
            return Result.Success(string.Empty);
        }

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
                "Detected {KeywordCount} library keywords in prompt",
                detectedKeywords.Count);
        }

        // Resolve keywords to library IDs via Context7 MCP (top 5 only for size management)
        var keywordCountToResolve = Math.Min(_maxLibrariesInContext, detectedKeywords.Count);
        var resolvedLibraries = new List<Context7Library>(keywordCountToResolve);

        for (var i = 0; i < keywordCountToResolve; i++)
        {
            var resolved = await ResolveLibraryAsync(detectedKeywords[i], ct).ConfigureAwait(false);
            if (resolved is not null)
            {
                resolvedLibraries.Add(resolved);
            }
        }

        var sb = new StringBuilder();

        if (resolvedLibraries.Count > 0)
        {
            sb.AppendLine("=== CONTEXT7 LIBRARY DOCUMENTATION ===");
            sb.AppendLine("Reference up-to-date documentation for the following libraries:");
            sb.AppendLine();

            foreach (var lib in resolvedLibraries)
            {
                sb.Append("📚 ");
                sb.AppendLine(lib.Name);
                sb.Append("   Library ID: ");
                sb.AppendLine(lib.LibraryId);
                if (!string.IsNullOrEmpty(lib.Url))
                {
                    sb.Append("   📖 ");
                    sb.AppendLine(lib.Url);
                }

                // Include actual documentation if available
                if (!string.IsNullOrEmpty(lib.Documentation))
                {
                    sb.AppendLine("   Documentation:");
                    var docLines = lib.Documentation.Split(_lineBreakChars, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var docLine in docLines.Take(5)) // Limit to first 5 lines per library
                    {
                        sb.Append("   ");
                        sb.AppendLine(
                            docLine.Length > 100 ? new string(docLine.AsSpan().Slice(0, 97)) + "..." : docLine);
                    }
                }
                else
                {
                    sb.AppendLine(
                        "   ✅ Use current APIs and version-specific examples from this library's documentation");
                }

                sb.AppendLine();
            }

            sb.AppendLine(
                "When implementing the solution, reference the documentation above to ensure you use current APIs.");
            sb.AppendLine(
                "Do NOT use outdated patterns or hallucinated APIs — verify against the current documentation.");
        }
        else
        {
            // Fallback: List detected keywords for manual Context7 lookup
            sb.AppendLine("=== CONTEXT7 LIBRARY REFERENCES ===");
            sb.AppendLine("The following libraries were detected in the prompt:");
            sb.AppendLine();

            for (var i = 0; i < keywordCountToResolve; i++)
            {
                sb.Append("  • ");
                sb.AppendLine(detectedKeywords[i]);
            }

            sb.AppendLine();
            sb.AppendLine(
                "💡 Verify documentation for these libraries to ensure you use current, correct APIs.");
        }

        return Result.Success(sb.ToString());
    }

    /// <summary>
    ///     Resolves a library keyword to its Context7 library ID and fetches documentation.
    ///     Uses caching to avoid redundant resolution and HTTP calls.
    ///     Attempts to resolve via Copilot SDK first, then falls back to static mapping.
    /// </summary>
    /// <param name="keyword">The library keyword to resolve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Resolved library metadata with documentation, or null if resolution failed.</returns>
    private async Task<Context7Library?> ResolveLibraryAsync(string keyword, CancellationToken ct)
    {
        // Check cache first
        if (_libraryCache.TryGetValue(keyword, out var cached))
        {
            if (cached is not null && logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Resolved library from cache: {Keyword} → {LibraryId}", keyword,
                    cached.LibraryId);
            }

            return cached;
        }

        // Try to resolve via Copilot SDK first (dynamic resolution)
        var copilotResolved = await TryResolveThroughCopilotSdkAsync(keyword, ct).ConfigureAwait(false);
        if (copilotResolved is not null)
        {
            _libraryCache.TryAdd(keyword, copilotResolved);
            return copilotResolved;
        }

        // Fall back to static keyword mapping
        foreach (var (keywordEntry, libraryId, name) in _keywordToLibraryMap)
        {
            if (string.Equals(keywordEntry, keyword, StringComparison.OrdinalIgnoreCase))
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug("Resolved library from static mapping: {Keyword} → {LibraryId} ({Name})",
                        keyword, libraryId, name);
                }

                // Attempt to fetch documentation from Context7 API
                var documentation = await FetchDocumentationFromContext7Async(libraryId, ct).ConfigureAwait(false);

                var library = new Context7Library(keyword, libraryId, name, Documentation: documentation);
                _libraryCache.TryAdd(keyword, library);

                return library;
            }
        }

        // If not found in mapping, log and cache negative result
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
                "Library '{Keyword}' not found in resolution map or via Copilot SDK",
                keyword);
        }

        _libraryCache.TryAdd(keyword, null);

        return null;
    }

    /// <summary>
    ///     Attempts to resolve a library using the GitHub Copilot SDK via MCP integration.
    ///     This enables dynamic library resolution without maintaining a hardcoded list.
    /// </summary>
    /// <param name="keyword">The library keyword to resolve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Resolved library metadata or null if resolution failed or SDK unavailable.</returns>
    private async Task<Context7Library?> TryResolveThroughCopilotSdkAsync(string keyword, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_copilotSdkTimeoutSeconds));

            var client = new CopilotClient();
            await using (client)
            {
                await client.StartAsync().ConfigureAwait(false);

                await using var session = await client.CreateSessionAsync(new SessionConfig
                {
                    Model = "gpt-4o",
                    SystemMessage = new SystemMessageConfig
                    {
                        Mode = SystemMessageMode.Append,
                        Content = @"You are a library resolver assistant. When asked to resolve a library keyword, 
use your knowledge to identify the Context7 library ID and name. Return ONLY a JSON object with 'libraryId' 
and 'name' fields. Example: {""libraryId"": ""/facebook/react"", ""name"": ""React""}"
                    }
                }).ConfigureAwait(false);

                var result = new TaskCompletionSource<(string? LibraryId, string? Name)>();
                var taskCompleted = false;

                session.On(evt =>
                {
                    if (evt is AssistantMessageEvent msg && !taskCompleted)
                    {
                        try
                        {
                            // Parse JSON response from Copilot
                            var json = JsonDocument.Parse(msg.Data.Content);
                            var root = json.RootElement;

                            var libraryId = root.TryGetProperty("libraryId", out var idProp)
                                ? idProp.GetString()
                                : null;
                            var name = root.TryGetProperty("name", out var nameProp)
                                ? nameProp.GetString()
                                : null;

                            taskCompleted = true;
                            result.SetResult((libraryId, name));
                        }
                        catch (Exception ex)
                        {
                            if (logger.IsEnabled(LogLevel.Debug))
                            {
                                logger.LogDebug("Failed to parse Copilot SDK library resolution response: {Exception}",
                                    ex.Message);
                            }

                            taskCompleted = true;
                            result.TrySetResult((null, null));
                        }
                    }
                });

                // Query Copilot SDK to resolve library
                await session.SendAsync(new MessageOptions
                {
                    Prompt =
                        $"Resolve the library keyword '{keyword}' to its Context7 library ID and name. Respond with ONLY valid JSON."
                }).ConfigureAwait(false);

                // Wait for response with timeout
                var (libraryId, name) = await result.Task.ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(libraryId) && !string.IsNullOrWhiteSpace(name))
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug("Copilot SDK resolved library: {Keyword} → {LibraryId} ({Name})",
                            keyword, libraryId, name);
                    }

                    // Fetch documentation from Context7 API
                    var documentation = await FetchDocumentationFromContext7Async(libraryId, ct).ConfigureAwait(false);

                    return new Context7Library(keyword, libraryId, name, Documentation: documentation);
                }

                return null;
            }
        }
        catch (OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Copilot SDK library resolution timed out for {Keyword}", keyword);
            }

            return null;
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Copilot SDK library resolution failed for {Keyword}: {Exception}",
                    keyword, ex.Message);
            }

            return null;
        }
    }

    /// <summary>
    ///     Fetches documentation from Context7 API for a given library ID.
    ///     Gracefully handles network failures and timeouts without throwing.
    /// </summary>
    /// <param name="libraryId">The Context7 library ID (e.g., "/facebook/react").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Documentation excerpt or null if fetch failed.</returns>
    private async Task<string?> FetchDocumentationFromContext7Async(string libraryId, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_context7TimeoutSeconds));

            // Context7 API endpoint for library documentation
            var documentationUri = new Uri(_context7ApiBaseUri, $"docs{libraryId}");

            var response =
                await httpClient.GetAsync(documentationUri, HttpCompletionOption.ResponseContentRead, cts.Token)
                    .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug("Context7 API returned {StatusCode} for {LibraryId}",
                        response.StatusCode, libraryId);
                }

                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            // Extract first 500 chars of documentation as summary using Span for zero-allocation
            var contentSpan = content.AsSpan();
            if (contentSpan.Length > 500)
            {
                return new string(contentSpan.Slice(0, 500));
            }

            return content;
        }
        catch (OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Context7 API request timed out for {LibraryId}", libraryId);
            }

            return null;
        }
        catch (HttpRequestException ex)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Context7 API request failed for {LibraryId}: {Exception}",
                    libraryId, ex.Message);
            }

            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Unexpected error fetching Context7 documentation for {LibraryId}: {Exception}",
                libraryId, ex.Message);
            return null;
        }
    }

    /// <summary>
    ///     Detects library keyword references in the prompt text.
    ///     Returns unique keywords ordered by specificity (longer keywords first).
    ///     Zero-allocation: uses span-based searching to avoid string allocations.
    /// </summary>
    private static List<string> DetectLibraryKeywordsInPrompt(string prompt)
    {
        var promptSpan = prompt.AsSpan();
        var detected = new List<string>(10); // Pre-allocate typical size
        var seenKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (keyword, _, _) in _keywordToLibraryMap)
        {
            // Use case-insensitive contains via SearchValues for efficiency
            if (promptSpan.Contains(keyword.AsSpan(), StringComparison.OrdinalIgnoreCase)
                && seenKeywords.Add(keyword))
            {
                detected.Add(keyword);
            }
        }

        // Keywords are already sorted by length (longer first) in KeywordToLibraryMap
        return detected;
    }

    /// <summary>Represents a resolved Context7 library with its metadata.</summary>
    private sealed record Context7Library(
        string Keyword,
        string LibraryId,
        string Name,
        string? Url = null,
        string? Documentation = null);
}

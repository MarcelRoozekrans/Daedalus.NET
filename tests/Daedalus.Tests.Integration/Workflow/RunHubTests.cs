using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Api.Hubs;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using Thalos;
using Thalos.Git;
using Thalos.Workflow;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     The console's live channel, <c>/hubs/workflow-runs</c>, on the real Api with the workflow engine on: who may
///     connect, that a committed change reaches a connected client as a four-property message, and that the bearer
///     token a browser puts in the query string reaches no exported span, no exported log record and no log line.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class RunHubTests(PostgresFixture fixture)
{
    private const string Developer = "a-developer";

    /// <summary>The gate the manufacture process parks at, and the signal it awaits there.</summary>
    private const string GateSignal = "human_approval";

    /// <summary>What the test server's WebSocket client throws when the upgrade is answered with a status instead.</summary>
    private const string HandshakeRefused = "Incomplete handshake, status code: ";

    /// <summary>The policy scheme test 4 authenticates with: test headers when present, the JWT bearer scheme otherwise.</summary>
    private const string HeaderOrBearer = "HeaderOrBearer";

    /// <summary>The log category of the JWT bearer handler.</summary>
    private const string JwtBearerCategory = "Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler";

    /// <summary>
    ///     No caller is refused with 401 and a caller without developer or admin with 403, while a developer connects
    ///     to the same host.
    ///     Red: remove [Authorize] from the hub: the anonymous connection opens.
    ///     Red: authorize the hub with TaskRead instead of WorkflowResume: the analyst's connection opens.
    ///     Red: do not map the hub: every connection is a 404, the developer's included.
    /// </summary>
    [Fact]
    public async Task A_caller_without_developer_or_admin_cannot_connect()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(fixture, Substitute.For<IAgentRuntime>());
        await using var anonymous = Connect(host, user: null, roles: null);
        await using var analyst = Connect(host, "an-analyst", "analyst");
        await using var developer = Connect(host, Developer, "developer");

        await FluentActions.Awaiting(() => anonymous.StartAsync()).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(HandshakeRefused + "401");
        await FluentActions.Awaiting(() => analyst.StartAsync()).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(HandshakeRefused + "403");
        await developer.StartAsync();
        developer.State.Should().Be(HubConnectionState.Connected);
    }

    /// <summary>
    ///     The hub is mapped to close a connection once the sign-in it opened with expires, so run changes stop
    ///     reaching a lapsed token and any new connection has to authenticate again.
    ///     Red: map the hub without CloseOnAuthenticationExpiration: the option is false on its endpoints.
    /// </summary>
    [Fact]
    public async Task The_hub_is_mapped_to_close_a_connection_whose_sign_in_expired()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(fixture, Substitute.For<IAgentRuntime>());

        // The hub's dispatcher options, which SignalR hangs on the hub's endpoints as metadata; not on every one.
        var options = host.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith(WorkflowRunsHub.Path, StringComparison.Ordinal) ?? false)
            .Select(e => e.Metadata.GetMetadata<HttpConnectionDispatcherOptions>())
            .OfType<HttpConnectionDispatcherOptions>()
            .ToList();

        options.Should().NotBeEmpty("the engine is on, so the hub is mapped with its options");
        options.Should().OnlyContain(o => o.CloseOnAuthenticationExpiration);
    }

    /// <summary>
    ///     A run parked at the gate and resumed through the API reaches a connected developer as a RunChanged message
    ///     for that run whose status is no longer Awaiting.
    ///     Red: do not register the broadcaster as IWorkflowRunObserver: no message arrives.
    ///     Red: do not map the hub: the connection fails.
    ///     Red: send an empty node: the CurrentNode assertion fails.
    /// </summary>
    [Fact]
    public async Task A_resume_through_the_API_reaches_a_connected_developer()
    {
        await using var host = await StartScriptedHostAsync();
        using var client = host.Client(Developer, "developer");
        var runId = await StartParkedRunAsync(host, client);
        var received = Channel.CreateUnbounded<WorkflowRunChanged>();
        await using var connection = Connect(host, Developer, "developer");
        using var subscription = connection.On<WorkflowRunChanged>(
            WorkflowRunsHub.RunChangedMethod, message => received.Writer.TryWrite(message));
        await connection.StartAsync();

        var resume = await client.PostAsJsonAsync(
            $"/api/workflow-runs/{runId}/resume", new ResumeWorkflowRunRequest(GateSignal, Payload: null));
        resume.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var moved = await ReadUntilAsync(received.Reader, m => m.Id == runId && !string.Equals(m.Status, "Awaiting", StringComparison.Ordinal));
        moved.Should().NotBeNull("a change of the resumed run should reach the developer within 10 s");
        moved!.CurrentNode.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    ///     A client reading the raw JSON sees exactly id, status, currentNode and awaitingSignal.
    ///     Red: add a property to WorkflowRunChanged: the property list has five names.
    ///     Red: do not register the broadcaster as IWorkflowRunObserver: no message arrives.
    /// </summary>
    [Fact]
    public async Task A_run_change_message_has_exactly_four_properties()
    {
        await using var host = await StartScriptedHostAsync();
        using var client = host.Client(Developer, "developer");
        var received = Channel.CreateUnbounded<JsonElement>();
        await using var connection = Connect(host, Developer, "developer", typed: false);
        using var subscription = connection.On<JsonElement>(
            WorkflowRunsHub.RunChangedMethod, message => received.Writer.TryWrite(message.Clone()));
        await connection.StartAsync();

        var runId = await StartParkedRunAsync(host, client);

        var message = await ReadUntilAsync(
            received.Reader,
            m => m.TryGetProperty("id", out var id) && id.TryGetGuid(out var value) && value == runId);
        message.ValueKind.Should().Be(JsonValueKind.Object, "a change of the started run should arrive within 10 s");
        message.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("id", "status", "currentNode", "awaitingSignal");
    }

    /// <summary>
    ///     A developer's connection and an anonymous one, each carrying a sentinel in access_token the way a browser
    ///     does, leave the sentinel in no exported span, no exported log record and no captured log line, with every
    ///     category logging at every level, the hosting diagnostics category configured on at Information, and the
    ///     instrumentation's own query redaction configured off. The anonymous connection authenticates through the real
    ///     JWT bearer scheme, so its token also passes JwtBearerHandler and IdentityModel. The request was seen: an exported ASP.NET Core span of the hub carries
    ///     access_token=Redacted, for the accepted connection and for the refused one, and both log views saw the hub
    ///     path.
    ///     Red: remove ServerQueryRedactionProcessor: the span scan names the url.query of the hub request.
    ///     Red: remove the hosting diagnostics filter, which this host's configuration sets to Information: the
    ///     exported-record scan names the Request starting line.
    ///     Red: the same, with the exported-record scan left out: the log line scan names the Request starting line.
    ///     Red: make the processor write Hidden instead of Redacted: no hub span is seen.
    ///     Red: connect the second client as the developer: no hub span has a 401.
    ///     Red: make the copying log exporter record nothing: the exported-record hub path check fails.
    ///     Red: make the capturing provider record nothing: the log line hub path check fails.
    ///     Red: delete the JwtBearerEvents wiring line in Program.cs: no token-validation failure is logged.
    /// </summary>
    [Fact]
    public async Task The_access_token_appears_in_no_span_and_no_log()
    {
        var accepted = "zqx-token-" + Guid.NewGuid().ToString("N");
        var refused = "zqx-token-" + Guid.NewGuid().ToString("N");
        var spans = new ConcurrentQueue<ExportedText>();
        var records = new ConcurrentQueue<ExportedText>();
        var lines = new CapturingLoggerProvider();

        await using var host = await ScratchWorkflowHost.StartAsync(
            fixture,
            Substitute.For<IAgentRuntime>(),
            settings: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Microsoft"] = "Trace",
                ["Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Information",

                // The ASP.NET Core instrumentation redacts every query value unless this is set. Set, only
                // ServerQueryRedactionProcessor stands between the token and url.query, as it must whatever the setting.
                ["OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION"] = "true",
            },
            configureServices: services =>
            {
                services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddProcessor(
                    new SimpleActivityExportProcessor(new CopyingSpanExporter(spans))));
                services.ConfigureOpenTelemetryLoggerProvider(logging => logging.AddProcessor(
                    new SimpleLogRecordExportProcessor(new CopyingLogExporter(records))));
                services.AddSingleton<ILoggerProvider>(lines);

                // A caller with the test headers is the header handler's; any other goes through the real JWT bearer
                // scheme, so the refused connection's token is read by the hub token reader and fails in
                // JwtBearerHandler and IdentityModel, whose own logging the scans below then cover.
                services.AddAuthentication().AddPolicyScheme(HeaderOrBearer, HeaderOrBearer, options =>
                    options.ForwardDefaultSelector = context =>
                        context.Request.Headers.ContainsKey(HeaderTestAuthHandler.UserHeader)
                            ? HeaderTestAuthHandler.SchemeName
                            : JwtBearerDefaults.AuthenticationScheme);
                services.PostConfigureAll<AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = HeaderOrBearer;
                    options.DefaultChallengeScheme = HeaderOrBearer;
                    options.DefaultScheme = HeaderOrBearer;
                });
            });

        await using (var developer = Connect(host, Developer, "developer", accessToken: accepted))
        {
            await developer.StartAsync();
            await developer.StopAsync();
        }

        await using (var anonymous = Connect(host, user: null, roles: null, accessToken: refused))
        {
            await FluentActions.Awaiting(() => anonymous.StartAsync()).Should().ThrowAsync<Exception>();
        }

        // The accepted connection's request span ends when its socket closes, after StopAsync returns: wait for both.
        var seen = await WaitUntilAsync(() => HubSpans(spans).Where(s => s.Texts.Any(IsRedactedQuery)).Skip(1).Any());
        host.Factory.Services.GetRequiredService<TracerProvider>().ForceFlush().Should().BeTrue();
        host.Factory.Services.GetRequiredService<LoggerProvider>().ForceFlush().Should().BeTrue();

        // The leak scans come first, so a leak is what a failure names, even when the wait above timed out.
        string[] sentinels = [accepted, refused];
        Leaks(spans, sentinels).Should().BeEmpty("no exported span may carry the access token");
        Leaks(records, sentinels).Should().BeEmpty("no exported log record may carry the access token");
        lines.Lines.Where(l => sentinels.Any(s => l.Contains(s, StringComparison.Ordinal)))
            .Should().BeEmpty("no log line may carry the access token");

        seen.Should().BeTrue("both hub requests should end as exported ASP.NET Core spans carrying access_token=Redacted");
        var hubSpans = HubSpans(spans);
        hubSpans.Should().Contain(s => s.Texts.Contains("401", StringComparer.Ordinal), "the refused connection's span must be among them");
        records.Should().Contain(r => r.Texts.Any(t => t.Contains(WorkflowRunsHub.Path, StringComparison.Ordinal)),
            "the exported log records must include the hub requests, or their scan proves nothing");
        lines.Lines.Should().Contain(l => l.Contains(WorkflowRunsHub.Path, StringComparison.Ordinal),
            "the captured log lines must include the hub requests, or their scan proves nothing");
        lines.Lines.Should().Contain(
            l => l.Contains(JwtBearerCategory, StringComparison.Ordinal)
                && l.Contains("Failed to validate the token", StringComparison.Ordinal),
            "the refused connection's token must have been read and rejected by the JWT bearer handler");
    }

    /// <summary>
    ///     A client of the hub over the host's test server: WebSockets only, no negotiate, the way the Web connects. The
    ///     test user travels in the headers <see cref="HeaderTestAuthHandler"/> reads. <paramref name="accessToken"/>,
    ///     when given, is put in the query string as <c>access_token</c>, as the browser client does for a WebSocket.
    ///     <paramref name="typed"/> false leaves out the message's JSON context, so a handler can read the raw JSON.
    /// </summary>
    private static HubConnection Connect(
        ScratchWorkflowHost host, string? user, string? roles, string? accessToken = null, bool typed = true)
    {
        var server = host.Factory.Server;
        var builder = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, WorkflowRunsHub.Path), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.WebSocketFactory = async (context, ct) =>
                {
                    var client = server.CreateWebSocketClient();
                    client.ConfigureRequest = request =>
                    {
                        if (user is not null)
                        {
                            request.Headers[HeaderTestAuthHandler.UserHeader] = user;
                        }

                        if (roles is not null)
                        {
                            request.Headers[HeaderTestAuthHandler.RolesHeader] = roles;
                        }
                    };
                    var uri = accessToken is null ? context.Uri : WithAccessToken(context.Uri, accessToken);
                    return await client.ConnectAsync(uri, ct);
                };
            });
        if (typed)
        {
            builder.AddJsonProtocol(options =>
                options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, WorkflowRunsJsonContext.Default));
        }

        return builder.Build();
    }

    /// <summary><paramref name="uri"/> with <c>access_token</c> added to its query, after any parameter it has.</summary>
    private static Uri WithAccessToken(Uri uri, string accessToken)
    {
        var query = uri.Query.Length > 1 ? uri.Query[1..] + "&" : "";
        return new UriBuilder(uri) { Query = query + "access_token=" + Uri.EscapeDataString(accessToken) }.Uri;
    }

    /// <summary>A host on the scripted model, with a fast outbox poll and a fake pull-request host, as <c>RunListEndpointTests</c> boots one.</summary>
    private Task<ScratchWorkflowHost> StartScriptedHostAsync()
    {
        var pullRequests = new FakePullRequestPublisher();
        return ScratchWorkflowHost.StartAsync(
            fixture,
            new ScriptedManufactureRuntime(),
            seed: [("README.md", "usage"), ("AGENT.md", "Run dotnet test.\n")],
            configureServices: services =>
            {
                services.RemoveAll<WorkflowOutboxDispatchOptions>();
                services.AddSingleton(new WorkflowOutboxDispatchOptions { PollingInterval = TimeSpan.FromMilliseconds(250) });
                services.RemoveAll<IPullRequestPublisher>();
                services.RemoveAll<IOpenPullRequestLookup>();
                services.AddSingleton<IPullRequestPublisher>(pullRequests);
                services.AddSingleton<IOpenPullRequestLookup>(pullRequests);
            });
    }

    /// <summary>Starts a run through <c>POST /api/workflow-runs</c> and waits until it is parked at the gate.</summary>
    private static async Task<Guid> StartParkedRunAsync(ScratchWorkflowHost host, HttpClient client)
    {
        var start = await client.PostAsJsonAsync(
            "/api/workflow-runs", new StartWorkflowRunRequest("add a health check endpoint", ScratchWorkflowHost.Repository));
        start.StatusCode.Should().Be(HttpStatusCode.Created);
        var runId = (await start.Content.ReadFromJsonAsync<StartWorkflowRunResponse>())!.RunId;
        var parked = await host.WaitForAsync(runId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate");
        parked.AwaitingSignal.Should().Be(GateSignal);
        return runId;
    }

    /// <summary>The first message that matches <paramref name="match"/> within 10 s, or the default when none does.</summary>
    private static async Task<T?> ReadUntilAsync<T>(ChannelReader<T> reader, Func<T, bool> match)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await foreach (var message in reader.ReadAllAsync(timeout.Token))
            {
                if (match(message))
                {
                    return message;
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // Nothing matched in time; the caller's assertion names what it waited for.
        }

        return default;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        return condition();
    }

    private static bool IsRedactedQuery(string text) => text.Contains("access_token=Redacted", StringComparison.Ordinal);

    /// <summary>The exported ASP.NET Core spans of a request to the hub.</summary>
    private static List<ExportedText> HubSpans(IEnumerable<ExportedText> spans) =>
        [.. spans.Where(s => string.Equals(s.Owner, "Microsoft.AspNetCore", StringComparison.Ordinal)
            && s.Texts.Contains(WorkflowRunsHub.Path, StringComparer.Ordinal))];

    private static List<string> Leaks(IEnumerable<ExportedText> items, IReadOnlyCollection<string> sentinels) =>
        [.. items.SelectMany(item => item.Texts
            .Where(text => sentinels.Any(s => text.Contains(s, StringComparison.Ordinal)))
            .Select(text => $"{item.Owner} carries '{text}'"))];

    private static IEnumerable<string> Flatten(object? value) =>
        value is System.Collections.IEnumerable items and not string
            ? items.Cast<object?>().Select(item => item?.ToString() ?? "")
            : [value?.ToString() ?? ""];

    /// <summary>One exported span or log record, as text: its source or category, and every string it carries.</summary>
    private sealed record ExportedText(string Owner, IReadOnlyList<string> Texts);

    /// <summary>
    ///     An exporter that copies every string a span carries at export, after every processor registered before it,
    ///     <c>AddServiceDefaults</c>'s redaction included, has run, as the OTLP exporter would see it.
    /// </summary>
    private sealed class CopyingSpanExporter(ConcurrentQueue<ExportedText> sink) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var span in batch)
            {
                var texts = new List<string>
                {
                    span.OperationName, span.DisplayName, span.StatusDescription ?? "", span.TraceStateString ?? "",
                };
                foreach (var tag in span.TagObjects)
                {
                    texts.Add(tag.Key);
                    texts.AddRange(Flatten(tag.Value));
                }

                foreach (var spanEvent in span.Events)
                {
                    texts.Add(spanEvent.Name);
                    foreach (var tag in spanEvent.Tags)
                    {
                        texts.Add(tag.Key);
                        texts.AddRange(Flatten(tag.Value));
                    }
                }

                foreach (var link in span.Links)
                {
                    foreach (var tag in link.Tags ?? [])
                    {
                        texts.Add(tag.Key);
                        texts.AddRange(Flatten(tag.Value));
                    }
                }

                foreach (var baggage in span.Baggage)
                {
                    texts.Add(baggage.Key);
                    texts.Add(baggage.Value ?? "");
                }

                sink.Enqueue(new ExportedText(span.Source.Name, texts));
            }

            return ExportResult.Success;
        }
    }

    /// <summary>An exporter that copies every string a log record carries at export: message, body, attributes, scopes and exception.</summary>
    private sealed class CopyingLogExporter(ConcurrentQueue<ExportedText> sink) : BaseExporter<LogRecord>
    {
        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (var record in batch)
            {
                var texts = new List<string> { record.CategoryName ?? "", record.FormattedMessage ?? "", record.Body ?? "" };
                foreach (var attribute in record.Attributes ?? [])
                {
                    texts.Add(attribute.Key);
                    texts.AddRange(Flatten(attribute.Value));
                }

                record.ForEachScope(
                    static (scope, state) =>
                    {
                        foreach (var item in scope)
                        {
                            state.Add(item.Key);
                            state.AddRange(Flatten(item.Value));
                        }
                    },
                    texts);
                texts.Add(record.Exception?.ToString() ?? "");
                sink.Enqueue(new ExportedText(record.CategoryName ?? "", texts));
            }

            return ExportResult.Success;
        }
    }

    /// <summary>A logging provider that keeps every line it is given, with its category, state values and exception.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => [.. _lines];

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join(' ', pairs.Select(p => $"{p.Key}={p.Value}"))
                    : "";
                lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {values} {exception}");
            }
        }
    }
}

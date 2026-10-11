using System.Diagnostics;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Thalos;
using Thalos.Git;
using Thalos.Testing;
using Thalos.Workflow;
using DomainTask = Daedalus.Domain.Entities.Task;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Phase 2.9 spec, Tests, "Privacy, end to end" and the wiring. A board task runs the real manufacture process to the
///     gate, is approved, publishes and succeeds. Only the model and the pull-request host are replaced. Every span is
///     captured. The run must show one trace per step, linked back to its start, with the host actions, the approval
///     and the token spend visible. No span and no metric may carry the task's text, the agent's text or the PR's text.
///     Two views are scanned: a <see cref="TelemetryCapture"/> on every source the host listens to, and the host's own
///     OpenTelemetry in-memory exporters, which see exactly what the production pipeline would export.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TelemetryEndToEndTests(PostgresFixture fixture)
{
    /// <summary>
    ///     The activity sources the production host listens to: <c>AspireExtensions.TracedSources</c> with the
    ///     <c>daedalus.*</c> wildcard spelled out, plus the ASP.NET Core and HttpClient instrumentation sources.
    /// </summary>
    private static readonly string[] Sources =
        ["thalos", WorkflowTelemetry.SourceName, "ZeroAlloc.Mediator", ManufactureTelemetry.SourceName, "Microsoft.AspNetCore", "System.Net.Http"];

    /// <summary>
    ///     The meters the production host collects: <c>AspireExtensions.Meters</c> with the <c>daedalus.*</c> wildcard
    ///     spelled out, plus the meters the ASP.NET Core and HttpClient instrumentation add.
    /// </summary>
    private static readonly string[] Meters =
    [
        "thalos", WorkflowTelemetry.SourceName, ManufactureTelemetry.SourceName, "System.Runtime", "System.Net.Http",
        "System.Net.NameResolution", "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Routing",
        "Microsoft.AspNetCore.Diagnostics", "Microsoft.AspNetCore.Server.Kestrel", "Microsoft.AspNetCore.Http.Connections",
        "Microsoft.AspNetCore.Authentication", "Microsoft.AspNetCore.Authorization", "Microsoft.AspNetCore.RateLimiting",
    ];

    private static readonly string[] Steps = ["implement", "review", "retrospect", "gate", "publish", "file-findings", "done"];

    /// <summary>
    ///     Red: drop the starter proxy from the enabled registration; no start span exists.
    ///     Red: delete the task-id SetTag; no request span carries the task id.
    ///     Red: drop the dispatch span; the step list is empty.
    ///     Red: parent each dispatch on the stored context instead of linking it; the trace-id assertion fails.
    ///     Red: drop the links argument; the link assertion fails.
    ///     Red: drop the host-action wrapping in the dispatcher; the publish host-action span is missing.
    ///     Red: drop the store proxy from AddWorkflowOrm; the resume span is missing.
    ///     Red: make ReviewHandoffWorkflowStore call RecordAsync; the token measurements are missing.
    ///     Red: drop thalos.workflow from TracedSources; the OpenTelemetry exporter has no dispatch span.
    ///     Red: drop the ASP.NET Core Filter; the exporter has a /health span.
    ///     Red: add a TraceTag of daedalus.work_intent on the title to IManufactureRunStarter.StartAsync; the content
    ///     assertion names the start span.
    ///     Red: tag the task title on the request span next to the task id; the content assertion names that span.
    ///     Red: tag the task title on a token measurement; the metric content assertion names the tokens instrument.
    ///     The dispatch, link, host-action and store-proxy lines are Thalos 0.15.0 internals that this repo cannot edit.
    ///     They were checked by changing the test's input instead: filter that span out of the captured list, or compare
    ///     the link's trace id with the empty id.
    /// </summary>
    [Fact]
    public async Task A_manufacture_run_is_traced_step_by_step_and_no_span_carries_its_content()
    {
        // The model name is a tag value on purpose: it is not content. It carries no sentinel, but it is unique.
        var marker = $"zqx{Guid.NewGuid():N}"[..13];
        var model = $"probe-model-{Guid.NewGuid():N}";
        var taskCode = $"TEL-{marker}";
        var title = $"Probe title {marker}";
        var description = $"Probe description {marker}: filter cancelled tasks out of the claim query";
        var prompt = $"Probe prompt {marker}: implement it carefully";
        var pullRequests = new FakePullRequestPublisher();
        var runtime = new ScriptedManufactureRuntime(new TurnUsage(1_200, 80, model) { CacheReadTokens = 900, CacheWriteTokens = 150 });
        var exported = new List<Activity>();
        var exportedMetrics = new List<Metric>();
        using var capture = new TelemetryCapture([.. Sources, .. Meters]);

        await using var host = await ScratchWorkflowHost.StartAsync(
            fixture,
            runtime,
            seed: [("README.md", "usage"), ("AGENT.md", "Run dotnet test.\n")],
            configureServices: services =>
            {
                services.RemoveAll<WorkflowOutboxDispatchOptions>();
                services.AddSingleton(new WorkflowOutboxDispatchOptions { PollingInterval = TimeSpan.FromMilliseconds(250) });
                services.RemoveAll<IPullRequestPublisher>();
                services.RemoveAll<IOpenPullRequestLookup>();
                services.AddSingleton<IPullRequestPublisher>(pullRequests);
                services.AddSingleton<IOpenPullRequestLookup>(pullRequests);
                services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddInMemoryExporter(exported));
                services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddInMemoryExporter(exportedMetrics));
            });

        var project = Project.Create(Guid.NewGuid(), "Sandbox", "The scratch remote", repositoryUrl: host.Remote.Url).Value;
        var task = DomainTask.Create(
            Guid.NewGuid(), project.Id, taskCode, title, description, Priority.Medium, "Testing", 1, Complexity.Medium, prompt).Value;
        project.AddTask(task).IsSuccess.Should().BeTrue();
        await using (var db = await host.Factory.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync())
        {
            db.Projects.Add(project);
            await db.SaveChangesAsync();
        }

        using var client = host.Client("a-developer", "developer");
        var start = await client.PostAsync($"/api/tasks/{task.Id}/manufacture", content: null);
        start.StatusCode.Should().Be(HttpStatusCode.Created);
        var runId = (await start.Content.ReadFromJsonAsync<StartWorkflowRunResponse>())!.RunId;
        await host.WaitForAsync(runId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate");
        var resume = await client.PostAsJsonAsync(
            $"/api/workflow-runs/{runId}/resume", new { signal = "human_approval", payload = (string?)null, applyStandingInstructions = true });
        resume.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await host.WaitForAsync(runId, r => r.Status == WorkflowStatus.Succeeded, "succeeded");
        (await client.GetAsync("/health")).IsSuccessStatusCode.Should().BeTrue();

        // The dispatch of the last step finishes after the run row reads Succeeded; wait for its span.
        await WaitUntilAsync(() => capture.Spans.Any(s => IsSpan(s, WorkflowTelemetry.DispatchSpanName)
            && IsTag(s, WorkflowTelemetry.RunIdTag, runId) && IsTag(s, WorkflowTelemetry.NodeTag, "done")));

        var spans = capture.Spans;

        // The start, and the request that made it.
        var startSpan = spans.Should().ContainSingle(s => IsSpan(s, ManufactureTelemetry.StartSpanName) && OfRun(s, runId)).Subject;
        startSpan.GetTagItem(ManufactureTelemetry.RepositoryTag).Should().Be(ScratchWorkflowHost.Repository);
        var requestSpan = spans.Should().ContainSingle(s => string.Equals(s.Source.Name, "Microsoft.AspNetCore", StringComparison.Ordinal)
            && IsTag(s, ManufactureTelemetry.TaskIdTag, task.Id)).Subject;
        requestSpan.TraceId.Should().Be(startSpan.TraceId);

        // One trace per step, each linked back into the start's trace.
        var dispatches = spans.Where(s => IsSpan(s, WorkflowTelemetry.DispatchSpanName) && OfRun(s, runId)).ToList();
        dispatches.Select(d => (string?)d.GetTagItem(WorkflowTelemetry.NodeTag)).Should().BeEquivalentTo(Steps);
        dispatches.Should().OnlyContain(d => d.TraceId != startSpan.TraceId && d.Links.Any(l => l.Context.TraceId == startSpan.TraceId));
        dispatches.Select(d => d.TraceId).Should().OnlyHaveUniqueItems();
        dispatches.Single(d => IsTag(d, WorkflowTelemetry.NodeTag, "gate"))
            .GetTagItem(WorkflowTelemetry.OutcomeTag).Should().Be(WorkflowTelemetry.OutcomeAwaiting);

        // The host action nests in its step; the approval is a resume span with its signal.
        var publish = dispatches.Single(d => IsTag(d, WorkflowTelemetry.NodeTag, "publish"));
        var hostActionName = WorkflowTelemetry.HostActionSpanName.Replace("{type}", "OpenPullRequestAction", StringComparison.Ordinal);
        spans.Should().Contain(s => IsSpan(s, hostActionName) && OfRun(s, runId) && s.TraceId == publish.TraceId);
        spans.Should().Contain(s => IsSpan(s, WorkflowTelemetry.StoreResumeSpanName) && OfRun(s, runId)
            && IsTag(s, WorkflowTelemetry.SignalTag, "human_approval"));

        // The token spend, from the live completion.
        capture.MeasurementsOf(ManufactureTelemetry.TokensMetricName)
            .Where(m => string.Equals(m.Tags.GetValueOrDefault(ManufactureTelemetry.ModelTag)?.ToString(), model, StringComparison.Ordinal))
            .Select(m => (Kind: m.Tags[ManufactureTelemetry.TokenKindTag], m.Value))
            .Should().BeEquivalentTo(new (object?, double)[] { ("input", 1_200), ("output", 80), ("cache_read", 900), ("cache_write", 150) });

        // The real host's OpenTelemetry pipeline exports the workflow spans, and not the health probe.
        exported.Should().Contain(a => IsSpan(a, WorkflowTelemetry.DispatchSpanName) && OfRun(a, runId));
        exported.Should().NotContain(a => string.Equals(a.Source.Name, "Microsoft.AspNetCore", StringComparison.Ordinal) && IsTag(a, "url.path", "/health"));

        // Not vacuous: every source that produced anything is in the list this test captured, so no span reaches the
        // production exporter without also being scanned below. The scratch host emits no thalos.turn span: the scripted
        // runtime replaces the agent runtime (preflight A6), so "thalos" shows only its skills spans.
        // Nor do the mediator or HttpClient sources fire on this path, so they are captured but not required here.
        exported.Select(a => a.Source.Name).Distinct(StringComparer.Ordinal).Should().BeSubsetOf(Sources);
        var seenSources = spans.Select(s => s.Source.Name).Distinct(StringComparer.Ordinal).ToList();
        seenSources.Should().Contain(
            ["thalos", WorkflowTelemetry.SourceName, ManufactureTelemetry.SourceName, "Microsoft.AspNetCore"],
            "the privacy scan below must have seen spans from each of these sources");

        host.Factory.Services.GetRequiredService<MeterProvider>().ForceFlush().Should().BeTrue();
        exportedMetrics.Select(m => m.MeterName).Distinct(StringComparer.Ordinal).Should().BeSubsetOf(Meters);
        exportedMetrics.Select(m => m.Name).Should().Contain(
            [ManufactureTelemetry.TokensMetricName, WorkflowTelemetry.RunsStartedName, WorkflowTelemetry.DispatchDurationName]);

        // No content anywhere: not in a span name, a tag, an event, a link, a status description, or a metric tag.
        pullRequests.LastBody.Should().NotBeNullOrEmpty();
        var forbidden = new List<string>
        {
            marker, taskCode, title, description, prompt, project.ProjectName, project.Description, host.Remote.Url, host.DataRoot,
            "filtered cancelled tasks", "the claim query ignored status", "TaskRepository.cs", "implement done",
            "Run dotnet test.", ScriptedManufactureRuntime.LearnedLine, pullRequests.LastBody!, pullRequests.LastTitle!,
        };
        forbidden.AddRange(runtime.Nodes.Select(runtime.TaskFor).Where(text => text.Length > 0));
        forbidden.Count.Should().BeGreaterThan(10);

        // The scanner itself reads what spans and metrics carry: it finds the one allowed value, the repository name.
        SpanLeaks(spans, [ScratchWorkflowHost.Repository]).Should().NotBeEmpty("the scanner must see tag values");
        MetricLeaks(exportedMetrics, [model]).Should().NotBeEmpty("the scanner must see metric tag values");

        SpanLeaks(spans, forbidden).Should().BeEmpty("no captured span may carry content");
        SpanLeaks(exported, forbidden).Should().BeEmpty("no exported span may carry content");
        MetricLeaks(capture.Measurements, forbidden).Should().BeEmpty("no captured measurement may carry content");
        MetricLeaks(exportedMetrics, forbidden).Should().BeEmpty("no exported metric may carry content");
    }

    private static bool IsSpan(Activity span, string name) =>
        string.Equals(span.OperationName, name, StringComparison.Ordinal);

    private static bool OfRun(Activity span, Guid runId) => IsTag(span, WorkflowTelemetry.RunIdTag, runId);

    private static bool IsTag(Activity span, string key, object expected) =>
        string.Equals(span.GetTagItem(key)?.ToString(), expected.ToString(), StringComparison.Ordinal);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }

    private static List<string> SpanLeaks(IEnumerable<Activity> spans, IReadOnlyCollection<string> forbidden)
    {
        var leaks = new List<string>();
        foreach (var span in spans)
        {
            var texts = new List<string?> { span.OperationName, span.DisplayName, span.StatusDescription, span.TraceStateString };
            foreach (var tag in span.TagObjects)
            {
                texts.Add(tag.Key);
                texts.AddRange(Flatten(tag.Value));
            }

            foreach (var baggage in span.Baggage)
            {
                texts.Add(baggage.Key);
                texts.Add(baggage.Value);
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

            leaks.AddRange(Hits($"span '{span.OperationName}' from '{span.Source.Name}'", texts, forbidden));
        }

        return leaks;
    }

    private static List<string> MetricLeaks(IEnumerable<CapturedMeasurement> measurements, IReadOnlyCollection<string> forbidden)
    {
        var leaks = new List<string>();
        foreach (var measurement in measurements)
        {
            var texts = new List<string?> { measurement.Instrument };
            foreach (var tag in measurement.Tags)
            {
                texts.Add(tag.Key);
                texts.AddRange(Flatten(tag.Value));
            }

            leaks.AddRange(Hits($"measurement '{measurement.Instrument}'", texts, forbidden));
        }

        return leaks;
    }

    private static List<string> MetricLeaks(IEnumerable<Metric> metrics, IReadOnlyCollection<string> forbidden)
    {
        var leaks = new List<string>();
        foreach (var metric in metrics)
        {
            var texts = new List<string?> { metric.Name, metric.Description, metric.Unit, metric.MeterName };
            foreach (var point in metric.GetMetricPoints())
            {
                foreach (var tag in point.Tags)
                {
                    texts.Add(tag.Key);
                    texts.AddRange(Flatten(tag.Value));
                }
            }

            leaks.AddRange(Hits($"metric '{metric.Name}' from '{metric.MeterName}'", texts, forbidden));
        }

        return leaks;
    }

    private static IEnumerable<string?> Flatten(object? value) =>
        value is System.Collections.IEnumerable items and not string
            ? items.Cast<object?>().Select(item => item?.ToString())
            : [value?.ToString()];

    private static IEnumerable<string> Hits(string owner, IEnumerable<string?> texts, IReadOnlyCollection<string> forbidden) =>
        texts.Where(text => !string.IsNullOrEmpty(text))
            .SelectMany(text => forbidden.Where(f => f.Length > 0 && text!.Contains(f, StringComparison.Ordinal))
                .Select(f => $"{owner} carries '{f}' in '{text}'"));
}

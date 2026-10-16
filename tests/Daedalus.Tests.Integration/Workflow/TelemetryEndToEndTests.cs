using System.Diagnostics;
using Daedalus.Agents.Decisions;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Application.DTOs.WorkflowRuns;
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
///     Spans are scanned through a <see cref="TelemetryCapture"/> on every source the host listens to. The host's own
///     OpenTelemetry in-memory exporters, which see exactly what the production pipeline would export, are checked for
///     their sources, and their metrics are scanned too.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TelemetryEndToEndTests(PostgresFixture fixture)
{
    /// <summary>
    ///     The activity sources the production host listens to: read from <c>AspireExtensions.TracedSources</c> itself, so the
    ///     list cannot drift, with its <c>daedalus.*</c> wildcard spelled out, plus the ASP.NET Core and HttpClient
    ///     instrumentation sources.
    /// </summary>
    private static readonly string[] Sources =
    [
        .. Production("TracedSources"), ManufactureTelemetry.SourceName, DecisionTelemetry.SourceName,
        "Microsoft.AspNetCore", "System.Net.Http",
    ];

    /// <summary>
    ///     The meters the production host collects: read from <c>AspireExtensions.Meters</c> itself, with the <c>daedalus.*</c>
    ///     wildcard spelled out, plus the meters the ASP.NET Core and HttpClient instrumentation add.
    /// </summary>
    private static readonly string[] Meters =
    [
        .. Production("Meters"), ManufactureTelemetry.SourceName, DecisionTelemetry.SourceName, "System.Net.Http",
        "System.Net.NameResolution", "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Routing",
        "Microsoft.AspNetCore.Diagnostics", "Microsoft.AspNetCore.Server.Kestrel", "Microsoft.AspNetCore.Http.Connections",
        "Microsoft.AspNetCore.Authentication", "Microsoft.AspNetCore.Authorization", "Microsoft.AspNetCore.RateLimiting",
    ];

    /// <summary>
    ///     What the host may export, written out by hand on purpose: the independent oracle for the subset checks below.
    ///     Reading the host's own lists there would make adding Npgsql to them pass its own check.
    /// </summary>
    private static readonly string[] AllowedSources =
    [
        "thalos", WorkflowTelemetry.SourceName, "ZeroAlloc.Mediator", ManufactureTelemetry.SourceName, DecisionTelemetry.SourceName,
        "Minos", "Microsoft.AspNetCore", "System.Net.Http",
    ];

    /// <summary>The meters the host may export, by hand, for the same reason as <see cref="AllowedSources"/>.</summary>
    private static readonly string[] AllowedMeters =
    [
        "thalos", WorkflowTelemetry.SourceName, ManufactureTelemetry.SourceName, DecisionTelemetry.SourceName, "Minos", "System.Runtime",
        "System.Net.Http", "System.Net.NameResolution", "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Routing",
        "Microsoft.AspNetCore.Diagnostics", "Microsoft.AspNetCore.Server.Kestrel", "Microsoft.AspNetCore.Http.Connections",
        "Microsoft.AspNetCore.Authentication", "Microsoft.AspNetCore.Authorization", "Microsoft.AspNetCore.RateLimiting",
    ];

    /// <summary>The host's own list, minus the wildcard, which the two Daedalus names above replace.</summary>
    private static string[] Production(string field) => [.. ((string[])typeof(Daedalus.ServiceDefaults.AspireExtensions)
        .GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!)
        .Where(n => !string.Equals(n, "daedalus.*", StringComparison.Ordinal))];

    private static readonly string[] Steps = ["implement", "review", "retrospect", "gate", "publish", "file-findings", "done"];

    /// <summary>
    ///     Red: drop the starter proxy from the enabled registration; no start span exists (start span ContainSingle).
    ///     Red: make TagStartSpan tag a different repository value; the repository tag assertion fails.
    ///     Red: delete the task-id SetTag; no request span carries the task id.
    ///     Red: set Activity.Current to null after the task-id SetTag; the start span roots a new trace and the request
    ///     span is no longer in it.
    ///     Red: set Activity.Current to null in ManufactureRunStarter just before the store start; the dispatch link
    ///     leaves the start trace and the link assertion fails.
    ///     Red: start a wrapper span before the store start; the store.start span's parent is no longer the start span.
    ///     Red: replace IWorkflowStore with the concrete OrmWorkflowStore after AddWorkflowOrm; the unproxied store
    ///     emits no store spans, so the store.start ContainSingle fails, and the resume span would be missing too.
    ///     Red: build WorkflowRunGateway over the unproxied OrmWorkflowStore; only the resume span is missing, so the
    ///     resume span assertion fails.
    ///     Red: make ReviewHandoffWorkflowStore call RecordAsync; the token measurements are missing.
    ///     Red: drop thalos.workflow from TracedSources; the OpenTelemetry exporter has no dispatch span.
    ///     Red: drop the ASP.NET Core Filter; the exporter has a /health span.
    ///     Red: add Npgsql to TracedSources; the exported-source subset check names it.
    ///     Red: drop thalos from the captured sources in this test; the seen-sources check names it.
    ///     Red: drop NodeSelectionRunner from the dispatcher's chain; the seen-sources check names daedalus.decisions.
    ///     Red: tag the work intent on the decision span; the content assertion names that span.
    ///     Red: add Npgsql to Meters; the exported-meter subset check fails.
    ///     Red: make Production return nothing; the capture lacks thalos, so the seen-sources check names it.
    ///     Red: drop daedalus.* from Meters; the exported metric names lack the tokens counter.
    ///     Red: make Flatten return nothing; the span scanner self-check fails.
    ///     Red: skip the metric points in the metric scanner; the metric scanner self-check fails.
    ///     Red: add a TraceTag of daedalus.work_intent on the title to IManufactureRunStarter.StartAsync; the content
    ///     assertion names the start span.
    ///     Red: tag the task title on the request span next to the task id; the content assertion names that span.
    ///     Red: tag the review evidence, or the retrospect reply, on the request span; the content assertion names it.
    ///     Red: tag the task title on a token measurement; the metric content assertion names the tokens instrument.
    ///     Red: add a sentinel to the token counter description; the exported-metric content assertion names it.
    ///     Red: wait for a node name that never runs; the done-span assertion fails with its message.
    ///     Task B11, a long intent. Red: leave WorkIntentRunner out of the dispatcher's chain; the implement and review
    ///     turns lack the tail, which lies past the variable's 512-character cut. Red: shorten the padding so the intent
    ///     is 700 characters or fewer; the length assertion fails. Red: tag the block's text on Activity.Current in
    ///     WorkIntentRunner; the content assertion names the dispatch span.
    ///     No exported-span scan exists: the exporter and the capture see the same Activity objects, and the subset check
    ///     keeps every exported source inside the captured ones, so a second span scan could never differ.
    ///     Guarded in Thalos 0.15.0, not here: the dispatch span, its own trace per dispatch, its host-action child, the
    ///     gate outcome and the completed outcome with no error type. They are Thalos internals that this repo cannot
    ///     edit. See Thalos.NET.Tests.Workflow DispatchTelemetryTests, A_dispatch_is_one_consumer_span_tagged_with_its_run_node_seq_process_and_outcome,
    ///     A_dispatch_under_an_ambient_activity_is_still_its_own_trace_and_leaves_the_ambient_current,
    ///     A_gate_dispatch_ends_awaiting, A_run_started_under_an_activity_links_its_dispatch_to_that_context,
    ///     and HostActionTelemetryTests. The assertions here only prove the wiring reached the real host.
    ///     Finding text is not exercised: every review lens approves on this path, so file-findings files nothing.
    /// </summary>
    [Fact]
    public async Task A_manufacture_run_is_traced_step_by_step_and_no_span_carries_its_content()
    {
        // The model name is a tag value on purpose: it is not content. It carries no sentinel, but it is unique.
        var marker = $"zqx{Guid.NewGuid():N}"[..13];
        var model = $"probe-model-{Guid.NewGuid():N}";
        var taskCode = $"TEL-{marker}";
        var title = $"Probe title {marker}";
        // Task B11: long enough that the work intent runs past the 512 characters Thalos renders of a variable, so the
        // full intent reaches the turns only through WorkIntentRunner's block, and its tail is scanned for on its own.
        var tail = $"Probe tail {marker}: the last sentence lies past the variable's cut.";
        var description = $"Probe description {marker}: filter cancelled tasks out of the claim query. "
            + string.Concat(Enumerable.Repeat("Keep the ordering and the paging of the claim query as they are. ", 9)) + tail;
        var prompt = $"Probe prompt {marker}: implement it carefully";
        var pullRequests = new FakePullRequestPublisher();
        var runtime = new ScriptedManufactureRuntime(new TurnUsage(1_200, 80, model) { CacheReadTokens = 900, CacheWriteTokens = 150 });
        var exported = new LockedCollection<Activity>();
        var exportedMetrics = new LockedCollection<Metric>();
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
                services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddInMemoryExporter((ICollection<Activity>)exported));
                services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddInMemoryExporter((ICollection<Metric>)exportedMetrics));
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
        var doneStopped = await WaitUntilAsync(() => capture.Spans.Any(s => IsSpan(s, WorkflowTelemetry.DispatchSpanName)
            && IsTag(s, WorkflowTelemetry.RunIdTag, runId) && IsTag(s, WorkflowTelemetry.NodeTag, "done")));
        doneStopped.Should().BeTrue("the done dispatch span must stop within 15 s of the run reading Succeeded");

        // One snapshot of each view. The listeners are process-wide, so spans of other tests keep arriving in the
        // exporter collection and the capture; nothing below reads the live ones again. The exporter is read first: the
        // capture sees every span the exporter does, so a span that stops between the two lines is captured but not
        // exported, and the exported-source subset check cannot fail on a span the capture lacks.
        var exportedSpans = exported.Snapshot();
        var spans = capture.Spans.ToList();
        var measurements = capture.Measurements.ToList();

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
        var storeStart = spans.Should().ContainSingle(s => IsSpan(s, WorkflowTelemetry.StoreStartSpanName) && OfRun(s, runId)).Subject;
        storeStart.ParentSpanId.Should().Be(startSpan.SpanId);
        dispatches.Should().OnlyContain(d => d.Links.Any(l => l.Context.SpanId == storeStart.SpanId));
        dispatches.Single(d => IsTag(d, WorkflowTelemetry.NodeTag, "gate"))
            .GetTagItem(WorkflowTelemetry.OutcomeTag).Should().Be(WorkflowTelemetry.OutcomeAwaiting);

        // The happy path of the other six: completed, no error status, no error type (preflight A9).
        var others = dispatches.Where(d => !IsTag(d, WorkflowTelemetry.NodeTag, "gate")).ToList();
        others.Should().HaveCount(Steps.Length - 1);
        others.Should().OnlyContain(d => IsTag(d, WorkflowTelemetry.OutcomeTag, WorkflowTelemetry.OutcomeCompleted)
            && d.Status != ActivityStatusCode.Error && d.GetTagItem(WorkflowTelemetry.ErrorTypeTag) == null);

        // The host action nests in its step; the approval is a resume span with its signal.
        var publish = dispatches.Single(d => IsTag(d, WorkflowTelemetry.NodeTag, "publish"));
        var hostActionName = WorkflowTelemetry.HostActionSpanName.Replace("{type}", "OpenPullRequestAction", StringComparison.Ordinal);
        spans.Should().Contain(s => IsSpan(s, hostActionName) && OfRun(s, runId) && s.ParentSpanId == publish.SpanId);
        spans.Should().Contain(s => IsSpan(s, WorkflowTelemetry.StoreResumeSpanName) && OfRun(s, runId)
            && IsTag(s, WorkflowTelemetry.SignalTag, "human_approval"));

        // The token spend, from the live completion.
        measurements.Where(m => string.Equals(m.Instrument, ManufactureTelemetry.TokensMetricName, StringComparison.Ordinal)
                && string.Equals(m.Tags.GetValueOrDefault(ManufactureTelemetry.ModelTag)?.ToString(), model, StringComparison.Ordinal))
            .Select(m => (Kind: m.Tags[ManufactureTelemetry.TokenKindTag], m.Value))
            .Should().BeEquivalentTo(new (object?, double)[] { ("input", 1_200), ("output", 80), ("cache_read", 900), ("cache_write", 150) });

        // The real host's OpenTelemetry pipeline exports the workflow spans, and not the health probe.
        exportedSpans.Should().Contain(a => IsSpan(a, WorkflowTelemetry.DispatchSpanName) && OfRun(a, runId));
        exportedSpans.Should().NotContain(a => string.Equals(a.Source.Name, "Microsoft.AspNetCore", StringComparison.Ordinal) && IsTag(a, "url.path", "/health"));

        // Not vacuous: every source that produced anything is in the list this test captured, so no span reaches the
        // production exporter without also being scanned below. The scratch host emits no thalos.turn span: the scripted
        // runtime replaces the agent runtime (preflight A6), so "thalos" shows only its skills spans.
        // Nor do the mediator or HttpClient sources fire on this path, so they are captured but not required here.
        var seenSources = spans.Select(s => s.Source.Name).Distinct(StringComparer.Ordinal).ToList();
        // Phase 2.10: every implement and review visit is decided, with decisions off as here, so the decision spans are
        // seen and scanned too.
        seenSources.Should().Contain(
            ["thalos", WorkflowTelemetry.SourceName, ManufactureTelemetry.SourceName, DecisionTelemetry.SourceName, "Microsoft.AspNetCore"],
            "the privacy scan below must have seen spans from each of these sources");
        exportedSpans.Select(a => a.Source.Name).Distinct(StringComparer.Ordinal).Should().BeSubsetOf(AllowedSources);

        host.Factory.Services.GetRequiredService<MeterProvider>().ForceFlush().Should().BeTrue();
        var exportedMetricList = exportedMetrics.Snapshot();
        exportedMetricList.Select(m => m.MeterName).Distinct(StringComparer.Ordinal).Should().BeSubsetOf(AllowedMeters);
        exportedMetricList.Select(m => m.Name).Should().Contain(
            [ManufactureTelemetry.TokensMetricName, WorkflowTelemetry.RunsStartedName, WorkflowTelemetry.DispatchDurationName]);

        // The long intent reached the turns in full, so the scan below covers a run that carried it.
        DomainTaskIntent(title, description).Length.Should().BeGreaterThan(700);
        runtime.TaskFor("implement").Should().Contain(tail, "the implement turn is handed the whole intent");
        runtime.TaskFor("review").Should().Contain(tail, "a review lens pass is handed the whole intent");

        // No content anywhere: not in a span name, a tag, an event, a link, a status description, or a metric tag.
        pullRequests.LastBody.Should().NotBeNullOrEmpty();
        var forbidden = new List<string>
        {
            marker, taskCode, title, description, prompt, project.ProjectName, project.Description, host.Remote.Url, host.DataRoot,
            "filtered cancelled tasks", "the claim query ignored status", "TaskRepository.cs", ScriptedManufactureRuntime.ReviewEvidence,
            "Run dotnet test.", ScriptedManufactureRuntime.LearnedLine, pullRequests.LastBody!, pullRequests.LastTitle!, tail,
        };
        forbidden.AddRange(runtime.Nodes.Select(node => $"{node} done"));
        forbidden.AddRange(runtime.Nodes.Select(runtime.TaskFor).Where(text => text.Length > 0));
        forbidden.Count.Should().BeGreaterThan(10);

        // The scanner itself reads what spans and metrics carry: it finds the one allowed value, the repository name.
        SpanLeaks(spans, [ScratchWorkflowHost.Repository]).Should().NotBeEmpty("the scanner must see tag values");
        MetricLeaks(exportedMetricList, [model]).Should().NotBeEmpty("the scanner must see metric tag values");

        SpanLeaks(spans, forbidden).Should().BeEmpty("no captured span may carry content");
        MetricLeaks(measurements, forbidden).Should().BeEmpty("no captured measurement may carry content");
        MetricLeaks(exportedMetricList, forbidden).Should().BeEmpty("no exported metric may carry content");
    }

    /// <summary>The work intent a board task starts its run with, as <c>TaskManufactureService</c> builds it.</summary>
    private static string DomainTaskIntent(string title, string description) => $"{title}\n\n{description}";

    private static bool IsSpan(Activity span, string name) =>
        string.Equals(span.OperationName, name, StringComparison.Ordinal);

    private static bool OfRun(Activity span, Guid runId) => IsTag(span, WorkflowTelemetry.RunIdTag, runId);

    private static bool IsTag(Activity span, string key, object expected) =>
        string.Equals(span.GetTagItem(key)?.ToString(), expected.ToString(), StringComparison.Ordinal);

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        return condition();
    }

    /// <summary>
    ///     The exporter target. The OpenTelemetry in-memory exporter adds to it, without a lock, on whichever thread stops
    ///     a span, and its listener is process-wide, so spans of parallel tests arrive while this test reads. Only
    ///     <see cref="Snapshot"/> reads it.
    /// </summary>
    private sealed class LockedCollection<T> : ICollection<T>
    {
        private readonly Lock _gate = new();
        private readonly List<T> _items = [];

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _items.Count;
                }
            }
        }

        public bool IsReadOnly => false;

        public IReadOnlyList<T> Snapshot()
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }

        public void Add(T item)
        {
            lock (_gate)
            {
                _items.Add(item);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _items.Clear();
            }
        }

        public bool Contains(T item)
        {
            lock (_gate)
            {
                return _items.Contains(item);
            }
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            lock (_gate)
            {
                _items.CopyTo(array, arrayIndex);
            }
        }

        public bool Remove(T item)
        {
            lock (_gate)
            {
                return _items.Remove(item);
            }
        }

        public IEnumerator<T> GetEnumerator() => Snapshot().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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

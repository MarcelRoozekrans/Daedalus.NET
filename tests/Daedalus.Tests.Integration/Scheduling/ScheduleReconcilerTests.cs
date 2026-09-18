using Daedalus.Agents.Scheduling;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Scheduling;

/// <summary>
///     Integration tests for <see cref="ScheduleReconciler.ReconcileAsync"/> against a real PostgreSQL database:
///     upserting the <c>ScheduledRuns</c> configuration array into the <c>ScheduledRuns</c> table, disabling
///     (never deleting) config rows dropped from configuration, leaving <see cref="ScheduleOrigin.Agent"/> rows
///     untouched, and validating every entry's cron and trigger before writing anything.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ScheduleReconcilerTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset _now = new(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Config_rows_are_inserted_on_first_run()
    {
        var configuration = BuildConfiguration(
            principalId: "schedule:daedalus",
            roles: ["reader"],
            entries: [new ConfigEntry("daily-digest", "0 7 * * *", "RepoDigest", "telegram", "482910337")]);

        await using var db = fixture.CreateDbContext();
        await ReconcilerFor(db, configuration).ReconcileAsync(default);

        var row = await LoadAsync("daily-digest");
        row.Cron.Should().Be("0 7 * * *");
        row.Trigger.Should().Be("RepoDigest");
        row.ChannelId.Should().Be("telegram");
        row.ConversationId.Should().Be("482910337");
        row.PrincipalId.Should().Be("schedule:daedalus");
        row.Roles.Should().Equal("reader");
        row.Origin.Should().Be(ScheduleOrigin.Config);
        row.Enabled.Should().BeTrue();
        row.NextRunAt.Should().Be(new DateTime(2026, 9, 16, 7, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Config_rows_are_updated_in_place_on_the_second_run_and_not_duplicated()
    {
        var firstRun = BuildConfiguration(
            "schedule:daedalus", ["reader"],
            [new ConfigEntry("daily-digest", "0 7 * * *", "RepoDigest", "telegram", "482910337")]);
        var secondRun = BuildConfiguration(
            "schedule:daedalus", ["reader"],
            [new ConfigEntry("daily-digest", "0 7 * * *", "RepoDigest", "telegram", "999999999")]);

        await using (var db = fixture.CreateDbContext())
        {
            await ReconcilerFor(db, firstRun).ReconcileAsync(default);
        }

        await using (var db = fixture.CreateDbContext())
        {
            await ReconcilerFor(db, secondRun).ReconcileAsync(default);
        }

        await using var verifyDb = fixture.CreateDbContext();
        var rows = await verifyDb.ScheduledRuns.Where(r => r.Name == "daily-digest").ToListAsync();
        rows.Should().ContainSingle("the second run must update the existing row, not insert a duplicate");
        rows[0].ConversationId.Should().Be("999999999");
    }

    [Fact]
    public async Task A_config_row_that_disappears_from_configuration_is_disabled_not_deleted()
    {
        var firstRun = BuildConfiguration(
            "schedule:daedalus", ["reader"],
            [new ConfigEntry("daily-digest", "0 7 * * *", "RepoDigest", "telegram", "482910337")]);
        var secondRun = BuildConfiguration("schedule:daedalus", ["reader"], []);

        await using (var db = fixture.CreateDbContext())
        {
            await ReconcilerFor(db, firstRun).ReconcileAsync(default);
        }

        await using (var db = fixture.CreateDbContext())
        {
            await ReconcilerFor(db, secondRun).ReconcileAsync(default);
        }

        var row = await LoadAsync("daily-digest");
        row.Should().NotBeNull("a config row missing from configuration is disabled, never deleted");
        row.Enabled.Should().BeFalse();
        row.Cron.Should().Be("0 7 * * *", "disabling must not touch the surviving config fields");
    }

    [Fact]
    public async Task A_config_row_whose_Enabled_flips_from_false_to_true_is_enabled_on_the_next_reconcile()
    {
        // The primary path: an operator ships the sample schedule disabled (Telegram never verified), later gets
        // a real chat id, and flips Enabled: true. Reconciliation must actually turn the schedule on, not just
        // refuse to turn it off again.
        var firstRun = BuildConfiguration(
            "schedule:daedalus", ["reader"],
            [new ConfigEntry("daily-digest", "0 7 * * *", "RepoDigest", "telegram", "482910337", Enabled: false)]);
        var secondRun = BuildConfiguration(
            "schedule:daedalus", ["reader"],
            [new ConfigEntry("daily-digest", "0 7 * * *", "RepoDigest", "telegram", "482910337", Enabled: true)]);

        await using (var db = fixture.CreateDbContext())
        {
            await ReconcilerFor(db, firstRun).ReconcileAsync(default);
        }

        (await LoadAsync("daily-digest")).Enabled.Should().BeFalse("sanity check: the first run must land disabled");

        await using (var db = fixture.CreateDbContext())
        {
            await ReconcilerFor(db, secondRun).ReconcileAsync(default);
        }

        (await LoadAsync("daily-digest")).Enabled.Should().BeTrue(
            "flipping Enabled: false to true in configuration is the intended way to turn a schedule on");
    }

    [Fact]
    public async Task A_config_row_removed_and_then_re_added_is_enabled_again()
    {
        var present = BuildConfiguration(
            "schedule:daedalus", ["reader"],
            [new ConfigEntry("daily-digest", "0 7 * * *", "RepoDigest", "telegram", "482910337")]);
        var removed = BuildConfiguration("schedule:daedalus", ["reader"], []);

        await using (var db = fixture.CreateDbContext())
        {
            await ReconcilerFor(db, present).ReconcileAsync(default);
        }

        await using (var db = fixture.CreateDbContext())
        {
            await ReconcilerFor(db, removed).ReconcileAsync(default);
        }

        (await LoadAsync("daily-digest")).Enabled.Should().BeFalse("sanity check: removal must disable the row");

        await using (var db = fixture.CreateDbContext())
        {
            await ReconcilerFor(db, present).ReconcileAsync(default);
        }

        (await LoadAsync("daily-digest")).Enabled.Should().BeTrue(
            "a schedule re-added to configuration is enabled again, not left disabled from its removal");
    }

    [Fact]
    public async Task Agent_origin_rows_are_never_touched_by_config_reconciliation()
    {
        var agentRun = ScheduledRun.Create(
            "agent-created", "0 8 * * *", "RepoDigest", "telegram", "111",
            "schedule:agent-created", ["reader"], ScheduleOrigin.Agent,
            new DateTime(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc)).Value;

        await using (var seedDb = fixture.CreateDbContext())
        {
            seedDb.ScheduledRuns.Add(agentRun);
            await seedDb.SaveChangesAsync();
        }

        // No config entries at all — if the reconciler ever queried Agent rows to decide what to disable,
        // this configuration (which does not name "agent-created") would flip it off.
        var configuration = BuildConfiguration("schedule:daedalus", ["reader"], []);

        await using var db = fixture.CreateDbContext();
        await ReconcilerFor(db, configuration).ReconcileAsync(default);

        var row = await LoadAsync("agent-created");
        row.Enabled.Should().BeTrue("Agent-origin rows must never be disabled by config reconciliation");
        row.Cron.Should().Be("0 8 * * *");
        row.NextRunAt.Should().Be(new DateTime(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc));
        row.Origin.Should().Be(ScheduleOrigin.Agent);
    }

    [Fact]
    public async Task An_unparseable_cron_expression_throws_at_startup()
    {
        var configuration = BuildConfiguration(
            "schedule:daedalus", ["reader"],
            [new ConfigEntry("bad-cron", "not a cron expression", "RepoDigest", "telegram", "482910337")]);

        await using var db = fixture.CreateDbContext();
        var act = async () => await ReconcilerFor(db, configuration).ReconcileAsync(default);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("bad-cron").And.Contain("not a cron expression");

        (await db.ScheduledRuns.CountAsync()).Should().Be(0, "an invalid entry must leave the database untouched");
    }

    [Fact]
    public async Task A_trigger_not_in_KnownTriggers_throws_at_startup()
    {
        var configuration = BuildConfiguration(
            "schedule:daedalus", ["reader"],
            [new ConfigEntry("daily-digest", "0 7 * * *", "RepoDigestSaga", "telegram", "482910337")]);

        await using var db = fixture.CreateDbContext();
        var act = async () => await ReconcilerFor(db, configuration).ReconcileAsync(default);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("daily-digest").And.Contain("RepoDigestSaga").And.Contain("RepoDigest");

        (await db.ScheduledRuns.CountAsync()).Should().Be(0, "an invalid entry must leave the database untouched");
    }

    private static ScheduleReconciler ReconcilerFor(ApplicationDbContext db, IConfiguration configuration) =>
        new(db, configuration, new FakeTimeProvider(_now), NullLogger<ScheduleReconciler>.Instance);

    private async Task<ScheduledRun> LoadAsync(string name)
    {
        await using var db = fixture.CreateDbContext();
        return await db.ScheduledRuns.SingleAsync(r => r.Name == name);
    }

    private sealed record ConfigEntry(
        string Name, string Cron, string Trigger, string ChannelId, string ConversationId, bool Enabled = true);

    private static IConfiguration BuildConfiguration(string principalId, string[] roles, IReadOnlyList<ConfigEntry> entries)
    {
        var dict = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DetachedRuns:PrincipalId"] = principalId,
        };

        for (var i = 0; i < roles.Length; i++)
        {
            dict[$"DetachedRuns:Roles:{i}"] = roles[i];
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            dict[$"ScheduledRuns:{i}:Name"] = entry.Name;
            dict[$"ScheduledRuns:{i}:Cron"] = entry.Cron;
            dict[$"ScheduledRuns:{i}:Trigger"] = entry.Trigger;
            dict[$"ScheduledRuns:{i}:ChannelId"] = entry.ChannelId;
            dict[$"ScheduledRuns:{i}:ConversationId"] = entry.ConversationId;
            dict[$"ScheduledRuns:{i}:Enabled"] = entry.Enabled.ToString();
        }

        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }
}

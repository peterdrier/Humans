using AwesomeAssertions;
using Humans.Agent.Data;
using Humans.Agent.Domain;
using Humans.Agent.Services;
using Humans.Agent.Services.Anthropic;
using Humans.Agent.Services.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;

namespace Humans.Agent.Tests;

/// <summary>
/// The retention rule itself, which moved from <c>AgentConversationRetentionJob</c> into the
/// section at the G5 move (nobodies-collective/Humans#866): the job is now a call plus a log
/// line, and the cutoff, the purge and the last-run record all live behind
/// <c>IAgentConversationRetention</c>. The job followed it into
/// <c>Humans.Agent/Contracts/</c> at G5 lane 5b-5.
/// </summary>
public class AgentConversationRetentionTests
{
    [HumansFact]
    public async Task Deletes_conversations_older_than_retention_days_only()
    {
        await using var db = InMemoryDb();
        var user = Guid.NewGuid();
        var now = Instant.FromUtc(2026, 4, 21, 3, 0);

        db.AgentConversations.Add(new AgentConversation { Id = Guid.NewGuid(), UserId = user, StartedAt = now - Duration.FromDays(200), LastMessageAt = now - Duration.FromDays(120), Locale = "es" });
        db.AgentConversations.Add(new AgentConversation { Id = Guid.NewGuid(), UserId = user, StartedAt = now - Duration.FromDays(30), LastMessageAt = now - Duration.FromDays(10), Locale = "es" });
        await db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var clock = new FakeClock(now);
        var repo = new AgentRepository(db, clock);
        var runStore = new AgentRetentionRunStore();

        var deleted = await MakeService(repo, runStore, clock, retentionDays: 90)
            .PurgeExpiredConversationsAsync(Xunit.TestContext.Current.CancellationToken);

        deleted.Should().Be(1);
        (await db.AgentConversations.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(1);
        runStore.Snapshot.LastRunAt.Should().Be(now);
        runStore.Snapshot.LastDeletedCount.Should().Be(1);
    }

    [HumansFact]
    public async Task Records_the_run_even_when_nothing_was_deleted()
    {
        // The admin status panel needs the timestamp to show the job is alive; a purge that
        // deletes nothing is the normal case on most nights.
        await using var db = InMemoryDb();
        var now = Instant.FromUtc(2026, 4, 21, 3, 0);
        var clock = new FakeClock(now);
        var runStore = new AgentRetentionRunStore();

        var deleted = await MakeService(new AgentRepository(db, clock), runStore, clock, retentionDays: 90)
            .PurgeExpiredConversationsAsync(Xunit.TestContext.Current.CancellationToken);

        deleted.Should().Be(0);
        runStore.Snapshot.LastRunAt.Should().Be(now);
        runStore.Snapshot.LastDeletedCount.Should().Be(0);
    }

    private static AgentService MakeService(
        IAgentRepository repo, IAgentRetentionRunStore runStore, IClock clock, int retentionDays)
    {
        var settings = Substitute.For<IAgentSettingsService>();
        settings.Current.Returns(new AgentSettingsDto(
            Enabled: true,
            Model: "test-model",
            PreloadConfig: default,
            DailyMessageCap: 30,
            HourlyMessageCap: 10,
            DailyTokenCap: 50_000,
            RetentionDays: retentionDays,
            UpdatedAt: clock.GetCurrentInstant()));

        // Everything below the retention path is substituted: purge touches settings, the
        // repository, the run store and the clock, and nothing else.
        return new AgentService(
            settings,
            Substitute.For<IAgentRateLimitStore>(),
            Substitute.For<IAgentAbuseDetector>(),
            repo,
            runStore,
            Substitute.For<IAgentUserSnapshotProvider>(),
            Substitute.For<IAgentPreloadCorpusBuilder>(),
            Substitute.For<IAgentPromptAssembler>(),
            Substitute.For<IAgentToolDispatcher>(),
            Substitute.For<IAnthropicClient>(),
            Options.Create(new AnthropicOptions()),
            clock,
            NullLogger<AgentService>.Instance);
    }

    private static AgentDbContext InMemoryDb() =>
        new(new DbContextOptionsBuilder<AgentDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class FakeClock(Instant now) : IClock { public Instant GetCurrentInstant() => now; }
}

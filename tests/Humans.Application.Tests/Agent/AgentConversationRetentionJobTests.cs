using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories;
using Humans.Infrastructure.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Agent;

public class AgentConversationRetentionJobTests
{
    [HumansFact]
    public async Task Deletes_conversations_older_than_retention_days_only()
    {
        await using var db = InMemoryDb();
        var user = Guid.NewGuid();
        var now = Instant.FromUtc(2026, 4, 21, 3, 0);

        db.AgentConversations.Add(new AgentConversation { Id = Guid.NewGuid(), UserId = user, StartedAt = now - Duration.FromDays(200), LastMessageAt = now - Duration.FromDays(120), Locale = "es" });
        db.AgentConversations.Add(new AgentConversation { Id = Guid.NewGuid(), UserId = user, StartedAt = now - Duration.FromDays(30), LastMessageAt = now - Duration.FromDays(10), Locale = "es" });
        await db.SaveChangesAsync();

        var settings = Substitute.For<IAgentSettingsService>();
        settings.Current.Returns(new AgentSettingsDto(
            Enabled: true,
            Model: "test-model",
            PreloadConfig: default,
            DailyMessageCap: 30,
            HourlyMessageCap: 10,
            DailyTokenCap: 50_000,
            RetentionDays: 90,
            UpdatedAt: now));

        var clock = new FakeClock(now);
        var repo = new AgentRepository(db, clock);
        var runStore = new AgentRetentionRunStore();
        var job = new AgentConversationRetentionJob(repo, settings, runStore, clock, NullLogger<AgentConversationRetentionJob>.Instance);
        await job.ExecuteAsync(CancellationToken.None);

        (await db.AgentConversations.CountAsync()).Should().Be(1);
        runStore.Snapshot.LastRunAt.Should().Be(now);
        runStore.Snapshot.LastDeletedCount.Should().Be(1);
    }

    private static HumansDbContext InMemoryDb() =>
        new(new DbContextOptionsBuilder<HumansDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class FakeClock(Instant now) : IClock { public Instant GetCurrentInstant() => now; }
}

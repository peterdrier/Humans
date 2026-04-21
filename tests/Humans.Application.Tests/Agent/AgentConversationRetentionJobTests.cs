using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Agent;

public class AgentConversationRetentionJobTests
{
    [Fact]
    public async Task Deletes_conversations_older_than_retention_days_only()
    {
        await using var db = InMemoryDb();
        var user = Guid.NewGuid();
        var now = Instant.FromUtc(2026, 4, 21, 3, 0);

        db.AgentConversations.Add(new AgentConversation { Id = Guid.NewGuid(), UserId = user, StartedAt = now - Duration.FromDays(200), LastMessageAt = now - Duration.FromDays(120), Locale = "es" });
        db.AgentConversations.Add(new AgentConversation { Id = Guid.NewGuid(), UserId = user, StartedAt = now - Duration.FromDays(30), LastMessageAt = now - Duration.FromDays(10), Locale = "es" });
        await db.SaveChangesAsync();

        var settings = Substitute.For<IAgentSettingsService>();
        settings.Current.Returns(new AgentSettings { RetentionDays = 90 });

        var job = new AgentConversationRetentionJob(db, settings, new FakeClock(now), NullLogger<AgentConversationRetentionJob>.Instance);
        await job.ExecuteAsync(CancellationToken.None);

        (await db.AgentConversations.CountAsync()).Should().Be(1);
    }

    private static HumansDbContext InMemoryDb() =>
        new(new DbContextOptionsBuilder<HumansDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class FakeClock(Instant now) : IClock { public Instant GetCurrentInstant() => now; }
}

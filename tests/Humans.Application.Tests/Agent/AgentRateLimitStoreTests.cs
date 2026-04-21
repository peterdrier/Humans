using AwesomeAssertions;
using Humans.Infrastructure.Stores;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Agent;

public class AgentRateLimitStoreTests
{
    [Fact]
    public void Incrementing_accumulates_for_the_same_user_and_day()
    {
        var store = new AgentRateLimitStore();
        var user = Guid.NewGuid();
        var day = new LocalDate(2026, 4, 21);

        store.Record(user, day, messagesDelta: 1, tokensDelta: 500);
        store.Record(user, day, messagesDelta: 1, tokensDelta: 700);

        var snapshot = store.Get(user, day);
        snapshot.MessagesToday.Should().Be(2);
        snapshot.TokensToday.Should().Be(1200);
    }

    [Fact]
    public void Different_days_are_independent()
    {
        var store = new AgentRateLimitStore();
        var user = Guid.NewGuid();

        store.Record(user, new LocalDate(2026, 4, 20), 3, 100);
        store.Record(user, new LocalDate(2026, 4, 21), 1, 50);

        store.Get(user, new LocalDate(2026, 4, 20)).MessagesToday.Should().Be(3);
        store.Get(user, new LocalDate(2026, 4, 21)).MessagesToday.Should().Be(1);
    }
}

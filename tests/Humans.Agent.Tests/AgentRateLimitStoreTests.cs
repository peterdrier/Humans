using AwesomeAssertions;
using Humans.Agent.Services.Stores;
using NodaTime;

namespace Humans.Agent.Tests;

public class AgentRateLimitStoreTests
{
    [HumansFact]
    public void Record_SameUserSameDay_Accumulates()
    {
        var store = new AgentRateLimitStore();
        var user = Guid.NewGuid();
        var day = new LocalDate(2026, 4, 21);

        store.Record(user, day, hour: 12, messagesDelta: 1, tokensDelta: 500);
        store.Record(user, day, hour: 12, messagesDelta: 1, tokensDelta: 700);

        var snapshot = store.Get(user, day, hour: 12);
        snapshot.MessagesToday.Should().Be(2);
        snapshot.TokensToday.Should().Be(1200);
        snapshot.MessagesThisHour.Should().Be(2);
    }

    [HumansFact]
    public void Record_DifferentDays_AreIndependent()
    {
        var store = new AgentRateLimitStore();
        var user = Guid.NewGuid();

        store.Record(user, new LocalDate(2026, 4, 20), hour: 9, messagesDelta: 3, tokensDelta: 100);
        store.Record(user, new LocalDate(2026, 4, 21), hour: 9, messagesDelta: 1, tokensDelta: 50);

        store.Get(user, new LocalDate(2026, 4, 20), hour: 9).MessagesToday.Should().Be(3);
        store.Get(user, new LocalDate(2026, 4, 21), hour: 9).MessagesToday.Should().Be(1);
    }

    [HumansFact]
    public void Record_BucketsOlderThanYesterday_AreEvicted()
    {
        var store = new AgentRateLimitStore();
        var user = Guid.NewGuid();

        // Day 1 — original record.
        store.Record(user, new LocalDate(2026, 4, 19), hour: 9, messagesDelta: 4, tokensDelta: 200);
        store.Get(user, new LocalDate(2026, 4, 19), hour: 9).MessagesToday.Should().Be(4);

        // Two days later — eviction triggers; yesterday is retained, anything older drops.
        store.Record(user, new LocalDate(2026, 4, 21), hour: 9, messagesDelta: 1, tokensDelta: 50);

        store.Get(user, new LocalDate(2026, 4, 19), hour: 9).MessagesToday.Should().Be(0,
            "buckets more than one day old must be evicted to bound memory growth");
        store.Get(user, new LocalDate(2026, 4, 21), hour: 9).MessagesToday.Should().Be(1);
    }

    [HumansFact]
    public void Record_HourChanges_ResetsOnlyHourlyBucket()
    {
        var store = new AgentRateLimitStore();
        var user = Guid.NewGuid();
        var day = new LocalDate(2026, 4, 21);

        store.Record(user, day, hour: 9, messagesDelta: 5, tokensDelta: 100);
        store.Record(user, day, hour: 10, messagesDelta: 2, tokensDelta: 50);

        var hourTen = store.Get(user, day, hour: 10);
        hourTen.MessagesToday.Should().Be(7);
        hourTen.MessagesThisHour.Should().Be(2);

        var hourNine = store.Get(user, day, hour: 9);
        hourNine.MessagesThisHour.Should().Be(5);
    }
}

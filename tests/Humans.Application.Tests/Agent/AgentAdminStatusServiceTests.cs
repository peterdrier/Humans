using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Stores;
using Humans.Application.Models;
using Humans.Application.Services.Agent;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories;
using Humans.Infrastructure.Stores;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Agent;

public class AgentAdminStatusServiceTests
{
    [HumansFact]
    public async Task Aggregates_messages_into_24h_7d_30d_windows()
    {
        await using var db = InMemoryDb();
        var now = Instant.FromUtc(2026, 5, 17, 12, 0);
        var clock = new FakeClock(now);

        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();
        var convA = SeedConversation(db, user1, now);
        var convB = SeedConversation(db, user2, now);

        // 24h window: one message at now-1h
        SeedMessage(db, convA.Id, user1, now - Duration.FromHours(1),
            prompt: 100, output: 50, cached: 200, fetched: ["agent", "tickets"]);
        // 7d window (and 30d): one message at now-5d
        SeedMessage(db, convA.Id, user1, now - Duration.FromDays(5),
            prompt: 1000, output: 500, cached: 0, refusalReason: "rate_limit");
        // 30d-only: one message at now-25d
        SeedMessage(db, convB.Id, user2, now - Duration.FromDays(25),
            prompt: 200, output: 100, cached: 0, fetched: ["agent"]);
        await db.SaveChangesAsync();

        var report = await BuildService(db, clock).GetStatusAsync(CancellationToken.None);

        // 24h: one message, one unique user, prompt=100, output=50, cached=200
        report.Usage24h.MessageCount.Should().Be(1);
        report.Usage24h.UniqueUserCount.Should().Be(1);
        report.Usage24h.PromptTokens.Should().Be(100);
        report.Usage24h.CachedTokens.Should().Be(200);

        // 7d: two messages, one unique user
        report.Usage7d.MessageCount.Should().Be(2);
        report.Usage7d.UniqueUserCount.Should().Be(1);

        // 30d: three messages, two unique users
        report.Usage30d.MessageCount.Should().Be(3);
        report.Usage30d.UniqueUserCount.Should().Be(2);

        // Refusal breakdown (7d) — one rate_limit
        report.Refusals7dCount.Should().Be(1);
        report.Refusals7d.Should().ContainSingle(r => r.Reason == "rate_limit" && r.Count == 1);

        // Top docs over 7d — "agent" fetched once (the 30d-only doc is outside this window)
        report.TopDocs7d.Should().ContainSingle(d => d.Slug == "agent" && d.Count == 1);
        report.TopDocs7d.Should().ContainSingle(d => d.Slug == "tickets" && d.Count == 1);

        // Top users 7d
        report.TopUsers7d.Should().ContainSingle(u => u.UserId == user1 && u.MessageCount == 2);
    }

    [HumansFact]
    public async Task Balance_unavailable_when_admin_key_missing()
    {
        await using var db = InMemoryDb();
        var clock = new FakeClock(Instant.FromUtc(2026, 5, 17, 12, 0));

        // Stand up the service with a balance provider that returns the
        // "unavailable" status as the production fallback path would.
        var balance = Substitute.For<IAgentAnthropicBalanceProvider>();
        balance.GetBalanceAsync(Arg.Any<CancellationToken>())
            .Returns(new AgentBalanceStatus(BalanceUsd: null, UnavailableReason: "Admin API key not configured"));

        var report = await BuildService(db, clock, balance: balance).GetStatusAsync(CancellationToken.None);

        report.Balance.BalanceUsd.Should().BeNull();
        report.Balance.UnavailableReason.Should().Be("Admin API key not configured");
    }

    [HumansFact]
    public async Task SettingsStoreWarm_false_when_UpdatedAt_is_MinValue()
    {
        await using var db = InMemoryDb();
        var clock = new FakeClock(Instant.FromUtc(2026, 5, 17, 12, 0));

        // Default store snapshot has UpdatedAt = MinValue until LoadAsync.
        var settings = Substitute.For<IAgentSettingsService>();
        settings.Current.Returns(new AgentSettingsDto(
            Enabled: false, Model: "claude-sonnet-4-6", PreloadConfig: AgentPreloadConfig.Tier1,
            DailyMessageCap: 30, HourlyMessageCap: 10, DailyTokenCap: 50_000,
            RetentionDays: 90, UpdatedAt: Instant.MinValue));

        var report = await BuildService(db, clock, settings: settings).GetStatusAsync(CancellationToken.None);
        report.SettingsStoreWarm.Should().BeFalse();
    }

    private static AgentAdminStatusService BuildService(
        HumansDbContext db, IClock clock,
        IAgentSettingsService? settings = null,
        IAgentAnthropicBalanceProvider? balance = null)
    {
        settings ??= MakeSettings();
        balance ??= MakeBalanceUnavailable();
        var repo = new AgentRepository(db, clock);
        var rate = new AgentRateLimitStore();
        var retention = new AgentRetentionRunStore();
        return new AgentAdminStatusService(repo, settings, rate, retention, balance, clock);
    }

    private static IAgentSettingsService MakeSettings()
    {
        var settings = Substitute.For<IAgentSettingsService>();
        settings.Current.Returns(new AgentSettingsDto(
            Enabled: true, Model: "claude-sonnet-4-6", PreloadConfig: AgentPreloadConfig.Tier1,
            DailyMessageCap: 30, HourlyMessageCap: 10, DailyTokenCap: 50_000,
            RetentionDays: 90, UpdatedAt: Instant.FromUtc(2026, 5, 1, 0, 0)));
        return settings;
    }

    private static IAgentAnthropicBalanceProvider MakeBalanceUnavailable()
    {
        var balance = Substitute.For<IAgentAnthropicBalanceProvider>();
        balance.GetBalanceAsync(Arg.Any<CancellationToken>())
            .Returns(new AgentBalanceStatus(BalanceUsd: null, UnavailableReason: "test"));
        return balance;
    }

    private static AgentConversation SeedConversation(HumansDbContext db, Guid userId, Instant now)
    {
        var conv = new AgentConversation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Locale = "es",
            StartedAt = now - Duration.FromDays(40),
            LastMessageAt = now,
            MessageCount = 0,
        };
        db.AgentConversations.Add(conv);
        return conv;
    }

    private static void SeedMessage(HumansDbContext db, Guid conversationId, Guid userId,
        Instant createdAt, int prompt, int output, int cached,
        string[]? fetched = null, string? refusalReason = null)
    {
        db.AgentMessages.Add(new AgentMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Role = AgentRole.Assistant,
            Content = string.Empty,
            CreatedAt = createdAt,
            PromptTokens = prompt,
            OutputTokens = output,
            CachedTokens = cached,
            Model = "claude-sonnet-4-6",
            DurationMs = 1200,
            FetchedDocs = fetched ?? Array.Empty<string>(),
            RefusalReason = refusalReason,
        });
    }

    private static HumansDbContext InMemoryDb() =>
        new(new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class FakeClock(Instant now) : IClock { public Instant GetCurrentInstant() => now; }
}

using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Agent.Services;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Agent.Data;
using Humans.Agent.Services.Stores;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NSubstitute;
using Humans.Agent.Domain;
using Humans.Agent.Models;
using Humans.Agent.Services.Anthropic;

namespace Humans.Agent.Tests;

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
        SeedMessage(db, convA.Id, now - Duration.FromHours(1),
            prompt: 100, output: 50, cached: 200, fetched: ["agent", "tickets"]);
        // 7d window (and 30d): one message at now-5d
        SeedMessage(db, convA.Id, now - Duration.FromDays(5),
            prompt: 1000, output: 500, cached: 0, refusalReason: "rate_limit");
        // 30d-only: one message at now-25d
        SeedMessage(db, convB.Id, now - Duration.FromDays(25),
            prompt: 200, output: 100, cached: 0, fetched: ["agent"]);
        await db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var report = await BuildService(db, clock).GetStatusAsync(Xunit.TestContext.Current.CancellationToken);

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
    public async Task Latency_panel_excludes_user_rows_and_zero_duration_refusals()
    {
        // nobodies-collective/Humans#990: user rows carry no duration at all, and
        // refusal/error rows (rate_limited, abuse_flag, "error" traces) are
        // structural zeros — none of them represent a timed provider turn. Mixing
        // them into AverageTurnMs/P95TurnMs dilutes both toward roughly half the
        // real figure. Only completed (non-refused) assistant rows should count.
        await using var db = InMemoryDb();
        var now = Instant.FromUtc(2026, 5, 17, 12, 0);
        var clock = new FakeClock(now);

        var user1 = Guid.NewGuid();
        var conv = SeedConversation(db, user1, now);

        // Real, completed assistant turns — the only rows that should feed the average/p95.
        SeedMessage(db, conv.Id, now - Duration.FromMinutes(30), prompt: 10, output: 5, cached: 0,
            role: AgentRole.Assistant, durationMs: 1000);
        SeedMessage(db, conv.Id, now - Duration.FromMinutes(20), prompt: 10, output: 5, cached: 0,
            role: AgentRole.Assistant, durationMs: 2000);

        // Dilution sources: a user row (no duration concept) and refusal/error assistant
        // rows (DurationMs stamped 0 because the turn never reached/finished the provider).
        SeedMessage(db, conv.Id, now - Duration.FromMinutes(31), prompt: 10, output: 0, cached: 0,
            role: AgentRole.User, durationMs: 0);
        SeedMessage(db, conv.Id, now - Duration.FromMinutes(10), prompt: 0, output: 0, cached: 0,
            role: AgentRole.Assistant, refusalReason: "rate_limited", durationMs: 0);
        SeedMessage(db, conv.Id, now - Duration.FromMinutes(5), prompt: 5, output: 3, cached: 0,
            role: AgentRole.Assistant, refusalReason: "error", durationMs: 0);
        await db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var report = await BuildService(db, clock).GetStatusAsync(Xunit.TestContext.Current.CancellationToken);

        // Average/p95 computed only over the two real assistant turns (1000ms, 2000ms).
        report.Usage24h.AverageTurnMs.Should().Be(1500);
        report.Usage24h.P95TurnMs.Should().Be(2000);

        // MessageCount still counts every row in the window (raw message volume), unaffected.
        report.Usage24h.MessageCount.Should().Be(5);
    }

    [HumansFact]
    public async Task Latency_panel_reports_zero_when_the_window_holds_no_completed_turn()
    {
        // Decoupling MessageCount from the duration sample made an empty sample reachable
        // with a positive message count: a window of nothing but user rows and refusals.
        // Averaging that empty sample throws, so the guard reads durations, not messages.
        await using var db = InMemoryDb();
        var now = Instant.FromUtc(2026, 5, 17, 12, 0);
        var clock = new FakeClock(now);

        var user1 = Guid.NewGuid();
        var conv = SeedConversation(db, user1, now);

        SeedMessage(db, conv.Id, now - Duration.FromMinutes(20), prompt: 10, output: 0, cached: 0,
            role: AgentRole.User, durationMs: 0);
        SeedMessage(db, conv.Id, now - Duration.FromMinutes(10), prompt: 0, output: 0, cached: 0,
            role: AgentRole.Assistant, refusalReason: "rate_limited", durationMs: 0);
        await db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var report = await BuildService(db, clock).GetStatusAsync(Xunit.TestContext.Current.CancellationToken);

        report.Usage24h.MessageCount.Should().Be(2);
        report.Usage24h.AverageTurnMs.Should().Be(0);
        report.Usage24h.P95TurnMs.Should().Be(0);
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

        var report = await BuildService(db, clock, balance: balance).GetStatusAsync(Xunit.TestContext.Current.CancellationToken);

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

        var report = await BuildService(db, clock, settings: settings).GetStatusAsync(Xunit.TestContext.Current.CancellationToken);
        report.SettingsStoreWarm.Should().BeFalse();
    }

    private static AgentAdminStatusService BuildService(
        AgentDbContext db, IClock clock,
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

    private static AgentConversation SeedConversation(AgentDbContext db, Guid userId, Instant now)
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

    private static void SeedMessage(AgentDbContext db, Guid conversationId,
        Instant createdAt, int prompt, int output, int cached,
        string[]? fetched = null, string? refusalReason = null,
        AgentRole role = AgentRole.Assistant, int durationMs = 1200)
    {
        db.AgentMessages.Add(new AgentMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Role = role,
            Content = string.Empty,
            CreatedAt = createdAt,
            PromptTokens = prompt,
            OutputTokens = output,
            CachedTokens = cached,
            Model = "claude-sonnet-4-6",
            DurationMs = durationMs,
            FetchedDocs = fetched ?? [],
            RefusalReason = refusalReason,
        });
    }

    private static AgentDbContext InMemoryDb() =>
        new(new DbContextOptionsBuilder<AgentDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class FakeClock(Instant now) : IClock { public Instant GetCurrentInstant() => now; }
}

using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Stores;
using Humans.Application.Models;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories;
using Humans.Infrastructure.Services.Agent;
using Humans.Infrastructure.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Agent;

public class AgentServiceTests
{
    [Fact]
    public async Task Ask_returns_rate_limit_finalizer_when_over_daily_cap()
    {
        var userId = Guid.NewGuid();
        var store = new AgentRateLimitStore();
        store.Record(userId, new LocalDate(2026, 4, 21), messagesDelta: 30, tokensDelta: 0);

        var svc = await BuildService(s =>
        {
            s.Enabled = true;
            s.DailyMessageCap = 30;
        }, rateLimitStore: store);

        var tokens = new List<AgentTurnToken>();
        await foreach (var t in svc.AskAsync(new AgentTurnRequest(
            ConversationId: Guid.Empty, UserId: userId, Message: "hi", Locale: "es"),
            CancellationToken.None))
        {
            tokens.Add(t);
        }

        // FIX 4 — use != null (not is not null pattern) to avoid expression tree issues
        tokens.Should().ContainSingle(t => t.Finalizer != null);
        tokens.Last().Finalizer!.StopReason.Should().Be("rate_limited");
    }

    // TODO(future): additional tests for disabled_returns_unavailable_finalizer, abuse_phrase_returns_refusal,
    // tool_loop_terminates_after_three_calls, handoff_records_FeedbackId, streaming_appends_message_rows.

    private static async Task<IAgentService> BuildService(
        Action<AgentSettings> tune,
        IAgentRateLimitStore? rateLimitStore = null)
    {
        var dbOptions = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new HumansDbContext(dbOptions);

        var settings = new AgentSettings
        {
            Id = 1, Enabled = false, Model = "claude-sonnet-4-6",
            PreloadConfig = AgentPreloadConfig.Tier1,
            DailyMessageCap = 30, HourlyMessageCap = 10, DailyTokenCap = 50_000, RetentionDays = 90,
            UpdatedAt = Instant.FromUtc(2026, 4, 21, 0, 0)
        };
        tune(settings);
        db.AgentSettings.Add(settings);
        await db.SaveChangesAsync();

        var clock = new FakeClock(Instant.FromUtc(2026, 4, 21, 12, 0));
        var store = new AgentSettingsStore();
        var settingsService = new AgentSettingsService(db, store, clock);
        await settingsService.LoadAsync(CancellationToken.None);

        var repo = new AgentConversationRepository(db, clock);
        var ratelimit = rateLimitStore ?? new AgentRateLimitStore();
        var abuse = new AgentAbuseDetector();
        var snapshots = Substitute.For<IAgentUserSnapshotProvider>();
        snapshots.LoadAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(
            new AgentUserSnapshot(Guid.NewGuid(), "Test User", "es", "Volunteer", true,
                Array.Empty<(string, string)>(), Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<Guid>(), Array.Empty<Guid>()));
        var preload = Substitute.For<IAgentPreloadCorpusBuilder>();
        preload.BuildAsync(Arg.Any<AgentPreloadConfig>(), Arg.Any<CancellationToken>()).Returns("");
        var assembler = new AgentPromptAssembler();
        var tools = Substitute.For<IAgentToolDispatcher>();
        var client = new AnthropicClientFake();
        var options = Options.Create(new Humans.Infrastructure.Configuration.AnthropicOptions());
        var logger = NullLogger<AgentService>.Instance;

        return new AgentService(settingsService, ratelimit, abuse, repo, snapshots, preload,
            assembler, tools, client, options, clock, logger);
    }

    private sealed class FakeClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }
}

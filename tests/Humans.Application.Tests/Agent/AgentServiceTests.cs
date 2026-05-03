using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Stores;
using Humans.Application.Models;
using Humans.Application.Services.Agent;
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
    [HumansFact]
    public async Task Ask_returns_rate_limit_finalizer_when_over_daily_cap()
    {
        var userId = Guid.NewGuid();
        var store = new AgentRateLimitStore();
        // Spread the 30 across hours so the daily cap is the binding constraint.
        for (var h = 0; h < 30; h++)
            store.Record(userId, new LocalDate(2026, 4, 21), hour: h, messagesDelta: 1, tokensDelta: 0);

        var (svc, _) = await BuildService(s =>
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

    [HumansFact]
    public async Task Ask_finalizer_carries_conversation_id_so_the_client_can_continue_the_thread()
    {
        var userId = Guid.NewGuid();
        var (svc, client) = await BuildService(s => s.Enabled = true);

        client.EnqueueTurn(
            new AgentTurnToken("hello!", null, null),
            new AgentTurnToken(null, null, new AgentTurnFinalizer(0, 0, 0, 0, "claude-sonnet-4-6", "end_turn")));

        var tokens = new List<AgentTurnToken>();
        await foreach (var t in svc.AskAsync(
            new AgentTurnRequest(ConversationId: Guid.Empty, UserId: userId, Message: "hi", Locale: "es"),
            CancellationToken.None))
        {
            tokens.Add(t);
        }

        var finalizer = tokens.Last().Finalizer!;
        finalizer.ConversationId.Should().NotBe(Guid.Empty,
            "the client uses the finalizer's ConversationId to continue the same server-side conversation on the next send");
    }

    [HumansFact]
    public async Task Ask_replays_prior_user_and_assistant_turns_to_the_model_when_the_conversation_continues()
    {
        var userId = Guid.NewGuid();
        var (svc, client) = await BuildService(s => s.Enabled = true);

        // Turn 1 — establishes the conversation.
        client.EnqueueTurn(
            new AgentTurnToken("Volunteers is a team.", null, null),
            new AgentTurnToken(null, null, new AgentTurnFinalizer(0, 0, 0, 0, "claude-sonnet-4-6", "end_turn")));

        Guid? conversationId = null;
        await foreach (var t in svc.AskAsync(
            new AgentTurnRequest(ConversationId: Guid.Empty, UserId: userId, Message: "What's Volunteers?", Locale: "es"),
            CancellationToken.None))
        {
            if (t.Finalizer is { } f) conversationId = f.ConversationId;
        }
        conversationId.Should().NotBe(Guid.Empty);

        // Turn 2 — same conversation. Anthropic should see the prior turn replayed.
        client.EnqueueTurn(
            new AgentTurnToken("Yes, anyone can join.", null, null),
            new AgentTurnToken(null, null, new AgentTurnFinalizer(0, 0, 0, 0, "claude-sonnet-4-6", "end_turn")));

        await foreach (var _ in svc.AskAsync(
            new AgentTurnRequest(ConversationId: conversationId!.Value, UserId: userId, Message: "Can I join?", Locale: "es"),
            CancellationToken.None))
        {
        }

        var lastRequest = client.LastRequest!;
        lastRequest.Messages.Should().HaveCount(3, "prior user+assistant turns plus the current user message must reach the model");
        lastRequest.Messages[0].Role.Should().Be("user");
        lastRequest.Messages[0].Text.Should().Be("What's Volunteers?");
        lastRequest.Messages[1].Role.Should().Be("assistant");
        lastRequest.Messages[1].Text.Should().Be("Volunteers is a team.");
        lastRequest.Messages[2].Role.Should().Be("user");
        lastRequest.Messages[2].Text.Should().Contain("Can I join?", "the current user message is on the tail of the message list");
    }

    [HumansFact]
    public async Task Ask_with_unknown_conversation_id_starts_a_new_conversation_instead_of_throwing()
    {
        var userId = Guid.NewGuid();
        var (svc, client) = await BuildService(s => s.Enabled = true);

        client.EnqueueTurn(
            new AgentTurnToken("hi back", null, null),
            new AgentTurnToken(null, null, new AgentTurnFinalizer(0, 0, 0, 0, "claude-sonnet-4-6", "end_turn")));

        Guid? finalConversationId = null;
        // Conversation may have been retention-purged or deleted in another tab —
        // a stale id from the client must not 500 the SSE stream.
        await foreach (var t in svc.AskAsync(
            new AgentTurnRequest(ConversationId: Guid.NewGuid(), UserId: userId, Message: "hi", Locale: "es"),
            CancellationToken.None))
        {
            if (t.Finalizer is { } f) finalConversationId = f.ConversationId;
        }

        finalConversationId.Should().NotBeNull("a fresh conversation must be started so the client can continue");
        finalConversationId.Should().NotBe(Guid.Empty);
    }

    [HumansFact]
    public async Task Rate_limited_refusal_is_not_written_into_another_users_conversation()
    {
        var attackerId = Guid.NewGuid();
        var victimId = Guid.NewGuid();

        // Trip the daily cap for the attacker before they call AskAsync.
        var store = new AgentRateLimitStore();
        for (var h = 0; h < 30; h++)
            store.Record(attackerId, new LocalDate(2026, 4, 21), hour: h, messagesDelta: 1, tokensDelta: 0);

        var (svc, _) = await BuildService(s =>
        {
            s.Enabled = true;
            s.DailyMessageCap = 30;
        }, rateLimitStore: store);

        // The repo behind the service is private; we can't reach the victim's
        // conversation directly. Instead, exercise the surface: the attacker
        // submits with a non-empty (and thus unknown-to-them) conversation id
        // and trips the rate limit. The refusal must NOT land on the supplied
        // GUID. Verified by reading the conversation back through the service:
        // it must either not exist or not belong to the attacker.
        var spoofedId = Guid.NewGuid();
        await foreach (var _ in svc.AskAsync(
            new AgentTurnRequest(ConversationId: spoofedId, UserId: attackerId, Message: "hi", Locale: "es"),
            CancellationToken.None))
        {
        }

        // The attacker's GET-history must not surface the spoofed id (because the
        // service started a new conversation owned by the attacker for the refusal).
        var attackerHistory = await svc.GetHistoryAsync(attackerId, take: 10, CancellationToken.None);
        attackerHistory.Should().NotContain(c => c.Id == spoofedId,
            "the rate-limit refusal must persist into a new attacker-owned conversation, never into the spoofed id");
        attackerHistory.Should().HaveCount(1, "exactly one new conversation was started for the refusal");
    }

    private static async Task<(IAgentService Svc, AnthropicClientFake Client)> BuildService(
        Action<AgentSettings> tune,
        IAgentRateLimitStore? rateLimitStore = null)
    {
        var dbOptions = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new HumansDbContext(dbOptions);

        var settings = new AgentSettings
        {
            Id = 1,
            Enabled = false,
            Model = "claude-sonnet-4-6",
            PreloadConfig = AgentPreloadConfig.Tier1,
            DailyMessageCap = 30,
            HourlyMessageCap = 10,
            DailyTokenCap = 50_000,
            RetentionDays = 90,
            UpdatedAt = Instant.FromUtc(2026, 4, 21, 0, 0)
        };
        tune(settings);
        db.AgentSettings.Add(settings);
        await db.SaveChangesAsync();

        var clock = new FakeClock(Instant.FromUtc(2026, 4, 21, 12, 0));
        var store = new AgentSettingsStore();
        var repo = new AgentRepository(db, clock);
        var settingsService = new AgentSettingsService(repo, store, clock);
        await settingsService.LoadAsync(CancellationToken.None);
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
        var options = Options.Create(new AnthropicOptions());
        var logger = NullLogger<AgentService>.Instance;

        var svc = new AgentService(settingsService, ratelimit, abuse, repo, snapshots, preload,
            assembler, tools, client, options, clock, logger);
        return (svc, client);
    }

    private sealed class FakeClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }
}

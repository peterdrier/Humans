using AwesomeAssertions;
using Humans.Application.Configuration;
using Humans.Application.Constants;
using Humans.Application.Interfaces;
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
            Xunit.TestContext.Current.CancellationToken))
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
            Xunit.TestContext.Current.CancellationToken))
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
            Xunit.TestContext.Current.CancellationToken))
        {
            if (t.Finalizer is { } f) conversationId = f.ConversationId;
        }
        conversationId.Should().NotBe(Guid.Empty);

        // Turn 2 — same conversation. Anthropic should see the prior turn replayed.
        client.EnqueueTurn(
            new AgentTurnToken("Yes, anyone can join.", null, null),
            new AgentTurnToken(null, null, new AgentTurnFinalizer(0, 0, 0, 0, "claude-sonnet-4-6", "end_turn")));

        await foreach (var _ in svc.AskAsync(
            new AgentTurnRequest(ConversationId: conversationId.Value, UserId: userId, Message: "Can I join?", Locale: "es"),
            Xunit.TestContext.Current.CancellationToken))
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
            Xunit.TestContext.Current.CancellationToken))
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
            Xunit.TestContext.Current.CancellationToken))
        {
        }

        // The attacker's GET-history must not surface the spoofed id (because the
        // service started a new conversation owned by the attacker for the refusal).
        var attackerHistory = await svc.GetHistoryAsync(attackerId, take: 10, Xunit.TestContext.Current.CancellationToken);
        attackerHistory.Should().NotContain(c => c.Id == spoofedId,
            "the rate-limit refusal must persist into a new attacker-owned conversation, never into the spoofed id");
        attackerHistory.Should().HaveCount(1, "exactly one new conversation was started for the refusal");
    }

    [HumansFact]
    public async Task PromptPreview_reports_system_prompt_token_count_from_count_tokens()
    {
        var userId = Guid.NewGuid();
        var (svc, client) = await BuildService(s => s.Enabled = true);
        client.CountTokensResult = 4096;

        var conversationId = await StartConversation(svc, client, userId);

        var preview = await svc.GetPromptPreviewForAdminAsync(
            conversationId, Xunit.TestContext.Current.CancellationToken);

        preview.Should().NotBeNull();
        preview.SystemPromptTokens.Should().Be(4096);
    }

    [HumansFact]
    public async Task PromptPreview_renders_without_a_token_count_when_count_tokens_fails()
    {
        var userId = Guid.NewGuid();
        var (svc, client) = await BuildService(s => s.Enabled = true);

        var conversationId = await StartConversation(svc, client, userId);

        client.CountTokensThrows = true;
        var preview = await svc.GetPromptPreviewForAdminAsync(
            conversationId, Xunit.TestContext.Current.CancellationToken);

        preview.Should().NotBeNull();
        preview.SystemPromptTokens.Should().BeNull(
            "a count_tokens failure must never break the admin prompt-preview page");
    }

    [HumansFact]
    public async Task Ask_synthesizes_a_final_answer_when_the_tool_call_cap_is_reached()
    {
        var userId = Guid.NewGuid();
        var dispatcher = Substitute.For<IAgentToolDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<AnthropicToolCall>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => new AnthropicToolResult(
                ci.Arg<AnthropicToolCall>().Id, "guide content", IsError: false));
        var rateLimitStore = new AgentRateLimitStore();
        var (svc, client) = await BuildService(s => s.Enabled = true,
            rateLimitStore: rateLimitStore, toolDispatcher: dispatcher);

        // Iteration 1: the model burns the whole tool budget (MaxToolCallsPerTurn = 3).
        client.EnqueueTurn(
            new AgentTurnToken("Let me look that up. ", null, null),
            new AgentTurnToken(null, new AnthropicToolCall("t1", "fetch_section_guide", """{"section":"teams"}"""), null),
            new AgentTurnToken(null, new AnthropicToolCall("t2", "fetch_section_guide", """{"section":"camps"}"""), null),
            new AgentTurnToken(null, new AnthropicToolCall("t3", "fetch_community_faq", "{}"), null),
            new AgentTurnToken(null, null, new AgentTurnFinalizer(100, 20, 5, 3, "claude-sonnet-4-6", "tool_use")));

        // Iteration 2: the synthesis call (tools withheld) answers from the fetched results.
        client.EnqueueTurn(
            new AgentTurnToken("Teams are groups of volunteers; see the Teams page.", null, null),
            new AgentTurnToken(null, null, new AgentTurnFinalizer(40, 30, 7, 2, "claude-sonnet-4-6", "end_turn")));

        var tokens = new List<AgentTurnToken>();
        await foreach (var t in svc.AskAsync(
            new AgentTurnRequest(ConversationId: Guid.Empty, UserId: userId, Message: "What are teams?", Locale: "es"),
            Xunit.TestContext.Current.CancellationToken))
        {
            tokens.Add(t);
        }

        var streamedText = string.Concat(tokens.Where(t => t.TextDelta != null).Select(t => t.TextDelta));
        streamedText.Should().Contain("Teams are groups of volunteers",
            "hitting the cap must still end in an answer synthesized from the tool results, not just the preamble");

        var finalizer = tokens.Last().Finalizer!;
        finalizer.StopReason.Should().Be("end_turn",
            "the synthesis call ends the turn normally");

        await dispatcher.Received(3).DispatchAsync(
            Arg.Any<AnthropicToolCall>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());

        client.LastRequest!.DisallowToolUse.Should().BeTrue(
            "the final synthesis call must withhold tool use so the model answers in text");

        // Token accounting must cover BOTH provider requests (tool-use + synthesis),
        // not just the last one — otherwise the tool-use request's tokens escape
        // admin spend and DailyTokenCap accounting.
        finalizer.InputTokens.Should().Be(140);
        finalizer.OutputTokens.Should().Be(50);
        finalizer.CacheReadTokens.Should().Be(12);
        finalizer.CacheCreationTokens.Should().Be(5);

        var transcript = await svc.GetConversationForUserAsync(
            userId, finalizer.ConversationId, Xunit.TestContext.Current.CancellationToken);
        var assistantMessage = transcript!.Messages.Single(m => m.Role == AgentRole.Assistant);
        assistantMessage.PromptTokens.Should().Be(140);
        assistantMessage.OutputTokens.Should().Be(50);
        assistantMessage.CachedTokens.Should().Be(12);

        rateLimitStore.Get(userId, new LocalDate(2026, 4, 21), hour: 12).TokensToday.Should().Be(190,
            "the daily token cap must count prompt+output tokens from every request in the turn");
    }

    [HumansFact]
    public async Task Route_to_issue_handoff_is_surfaced_by_the_admin_handoffs_filter()
    {
        var userId = Guid.NewGuid();
        var dispatcher = Substitute.For<IAgentToolDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<AnthropicToolCall>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new AnthropicToolResult(
                call.Arg<AnthropicToolCall>().Id, "Proposal queued.", IsError: false)));
        var (svc, client) = await BuildService(s => s.Enabled = true, toolDispatcher: dispatcher);

        // Turn 1 — plain answer, no handoff. Must NOT match the handoffs filter.
        await StartConversation(svc, client, Guid.NewGuid());

        // Turn 2 — the agent hands off via route_to_issue.
        client.EnqueueTurn(
            new AgentTurnToken("Let me draft an issue for you.", null, null),
            new AgentTurnToken(null, new AnthropicToolCall(
                "tc1", AgentToolNames.RouteToIssue,
                """{"title":"Broken link","category":"Bug","description":"The camps page 404s."}"""), null),
            new AgentTurnToken(null, null, new AgentTurnFinalizer(0, 0, 0, 0, "claude-sonnet-4-6", "tool_use")));

        Guid handoffConversationId = Guid.Empty;
        await foreach (var t in svc.AskAsync(
            new AgentTurnRequest(ConversationId: Guid.Empty, UserId: userId, Message: "the camps page is broken", Locale: "es"),
            Xunit.TestContext.Current.CancellationToken))
        {
            if (t.Finalizer is { } f) handoffConversationId = f.ConversationId;
        }

        var rows = await svc.ListAllConversationsForAdminWithMessagesAsync(
            refusalsOnly: false, handoffsOnly: true, userId: null, take: 50, skip: 0,
            Xunit.TestContext.Current.CancellationToken);

        var row = rows.Should().ContainSingle(
            "only the route_to_issue conversation is a handoff").Subject;
        row.Id.Should().Be(handoffConversationId);
        // Same predicate the API uses for HandoffCount (nobodies-collective/Humans#931).
        row.Messages.Count(m => m.HandedOffToFeedbackId != null
                || m.FetchedDocs.Contains(AgentToolNames.RouteToIssue, StringComparer.Ordinal))
            .Should().BeGreaterThan(0, "the saved assistant message must carry the handoff marker");
    }

    [HumansFact]
    public async Task Failed_route_to_issue_dispatch_does_not_count_as_a_handoff()
    {
        var userId = Guid.NewGuid();
        var dispatcher = Substitute.For<IAgentToolDispatcher>();
        // Malformed args → AgentToolDispatcher returns IsError=true and no
        // issueProposal frame is emitted, so the user never sees the Issues modal.
        dispatcher.DispatchAsync(Arg.Any<AnthropicToolCall>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new AnthropicToolResult(
                call.Arg<AnthropicToolCall>().Id, "Malformed tool arguments (expected JSON object).", IsError: true)));
        var (svc, client) = await BuildService(s => s.Enabled = true, toolDispatcher: dispatcher);

        client.EnqueueTurn(
            new AgentTurnToken(null, new AnthropicToolCall(
                "tc1", AgentToolNames.RouteToIssue, """{"title":"Broken"""), null),
            new AgentTurnToken(null, null, new AgentTurnFinalizer(0, 0, 0, 0, "claude-sonnet-4-6", "tool_use")));
        // The loop continues after the failed tool call; script the follow-up turn.
        client.EnqueueTurn(
            new AgentTurnToken("Sorry, I could not draft that issue.", null, null),
            new AgentTurnToken(null, null, new AgentTurnFinalizer(0, 0, 0, 0, "claude-sonnet-4-6", "end_turn")));

        await foreach (var _ in svc.AskAsync(
            new AgentTurnRequest(ConversationId: Guid.Empty, UserId: userId, Message: "the camps page is broken", Locale: "es"),
            Xunit.TestContext.Current.CancellationToken))
        {
        }

        var rows = await svc.ListAllConversationsForAdminWithMessagesAsync(
            refusalsOnly: false, handoffsOnly: true, userId: null, take: 50, skip: 0,
            Xunit.TestContext.Current.CancellationToken);
        rows.Should().BeEmpty("a failed route_to_issue dispatch never showed the user the Issues modal");

        // Same predicate the API uses for HandoffCount — must stay at zero.
        var all = await svc.ListAllConversationsForAdminWithMessagesAsync(
            refusalsOnly: false, handoffsOnly: false, userId: null, take: 50, skip: 0,
            Xunit.TestContext.Current.CancellationToken);
        all.Should().ContainSingle().Which.Messages
            .Count(m => m.HandedOffToFeedbackId != null
                || m.FetchedDocs.Contains(AgentToolNames.RouteToIssue, StringComparer.Ordinal))
            .Should().Be(0, "failed handoff attempts must not carry the handoff marker");
    }

    [HumansFact]
    public async Task Refused_conversations_are_surfaced_by_the_admin_refusals_filter()
    {
        var userId = Guid.NewGuid();
        var store = new AgentRateLimitStore();
        for (var h = 0; h < 30; h++)
            store.Record(userId, new LocalDate(2026, 4, 21), hour: h, messagesDelta: 1, tokensDelta: 0);

        var (svc, client) = await BuildService(s =>
        {
            s.Enabled = true;
            s.DailyMessageCap = 30;
        }, rateLimitStore: store);

        // A second user with a normal, non-refused conversation. Must NOT match the filter.
        await StartConversation(svc, client, Guid.NewGuid());

        // The capped user trips the rate limit — PersistRefusal writes RefusalReason.
        await foreach (var _ in svc.AskAsync(
            new AgentTurnRequest(ConversationId: Guid.Empty, UserId: userId, Message: "hi", Locale: "es"),
            Xunit.TestContext.Current.CancellationToken))
        {
        }

        var rows = await svc.ListAllConversationsForAdminWithMessagesAsync(
            refusalsOnly: true, handoffsOnly: false, userId: null, take: 50, skip: 0,
            Xunit.TestContext.Current.CancellationToken);

        var row = rows.Should().ContainSingle("only the rate-limited conversation is a refusal").Subject;
        row.UserId.Should().Be(userId);
        row.Messages.Should().Contain(m => m.RefusalReason == "rate_limited",
            "the API's RefusalCount projects from RefusalReason");
    }

    private static async Task<Guid> StartConversation(
        IAgentService svc, AnthropicClientFake client, Guid userId)
    {
        client.EnqueueTurn(
            new AgentTurnToken("hi", null, null),
            new AgentTurnToken(null, null, new AgentTurnFinalizer(0, 0, 0, 0, "claude-sonnet-4-6", "end_turn")));

        var conversationId = Guid.Empty;
        await foreach (var t in svc.AskAsync(
            new AgentTurnRequest(ConversationId: Guid.Empty, UserId: userId, Message: "hi", Locale: "es"),
            Xunit.TestContext.Current.CancellationToken))
        {
            if (t.Finalizer is { } f) conversationId = f.ConversationId;
        }
        return conversationId;
    }

    private static async Task<(IAgentService Svc, AnthropicClientFake Client)> BuildService(
        Action<AgentSettings> tune,
        IAgentRateLimitStore? rateLimitStore = null,
        IAgentToolDispatcher? toolDispatcher = null)
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
        await db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var clock = new FakeClock(Instant.FromUtc(2026, 4, 21, 12, 0));
        var store = new AgentSettingsStore();
        var repo = new AgentRepository(db, clock);
        var settingsService = new AgentSettingsService(repo, store, clock);
        await settingsService.LoadAsync(Xunit.TestContext.Current.CancellationToken);
        var ratelimit = rateLimitStore ?? new AgentRateLimitStore();
        var abuse = new AgentAbuseDetector();
        var snapshots = Substitute.For<IAgentUserSnapshotProvider>();
        snapshots.LoadAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(
            new AgentUserSnapshot(Guid.NewGuid(), "Test User", "es", "Volunteer", true,
                [],
                [],
                [],
                [], [],
                []));
        var preload = Substitute.For<IAgentPreloadCorpusBuilder>();
        preload.BuildAsync(Arg.Any<AgentPreloadConfig>(), Arg.Any<CancellationToken>()).Returns("");
        var assembler = new AgentPromptAssembler();
        var tools = toolDispatcher ?? Substitute.For<IAgentToolDispatcher>();
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

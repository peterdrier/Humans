using AwesomeAssertions;
using Humans.Application.Configuration;
using Humans.Application.Constants;
using Humans.Application.Interfaces;
using Humans.Agent.Services.Stores;
using Humans.Agent.Services;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Agent.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Humans.Agent.Domain;
using Humans.Agent.Models;
using Humans.Agent.Services.Anthropic;

namespace Humans.Agent.Tests;

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
    public async Task Ask_yields_fallback_text_and_logs_warning_when_the_turn_produces_no_assistant_text()
    {
        // nobodies-collective/Humans#952 — a truncated/exhausted tool loop must never
        // leave the user with a blank bubble or persist an empty Content string.
        var userId = Guid.NewGuid();
        var dispatcher = Substitute.For<IAgentToolDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<AnthropicToolCall>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => new AnthropicToolResult(
                ci.Arg<AnthropicToolCall>().Id, "guide content", IsError: false));
        var logger = Substitute.For<ILogger<AgentService>>();
        var (svc, client) = await BuildService(s => s.Enabled = true,
            toolDispatcher: dispatcher, logger: logger);

        // Iteration 1: the model burns the whole tool budget (MaxToolCallsPerTurn = 3) with no prose at all.
        client.EnqueueTurn(
            new AgentTurnToken(null, new AnthropicToolCall("t1", "fetch_section_guide", """{"section":"teams"}"""), null),
            new AgentTurnToken(null, new AnthropicToolCall("t2", "fetch_section_guide", """{"section":"camps"}"""), null),
            new AgentTurnToken(null, new AnthropicToolCall("t3", "fetch_community_faq", "{}"), null),
            new AgentTurnToken(null, null, new AgentTurnFinalizer(100, 20, 5, 3, "claude-sonnet-4-6", "tool_use")));

        // Iteration 2: the synthesis call (tools withheld) still produces no text — the
        // investigation dead-ended with nothing worth saying.
        client.EnqueueTurn(
            new AgentTurnToken(null, null, new AgentTurnFinalizer(40, 5, 0, 0, "claude-sonnet-4-6", "end_turn")));

        var tokens = new List<AgentTurnToken>();
        await foreach (var t in svc.AskAsync(
            new AgentTurnRequest(ConversationId: Guid.Empty, UserId: userId, Message: "What are teams?", Locale: "es"),
            Xunit.TestContext.Current.CancellationToken))
        {
            tokens.Add(t);
        }

        var streamedText = string.Concat(tokens.Where(t => t.TextDelta != null).Select(t => t.TextDelta));
        streamedText.Should().NotBeNullOrEmpty(
            "the user must see fallback prose instead of a blank bubble when the turn produced no text");
        streamedText.Should().Contain("reformular",
            "the fallback must respect the conversation locale (es), not stream English");

        var finalizer = tokens.Last().Finalizer!;
        var transcript = await svc.GetConversationForUserAsync(
            userId, finalizer.ConversationId, Xunit.TestContext.Current.CancellationToken);
        var assistantMessage = transcript!.Messages.Single(m => m.Role == AgentRole.Assistant);
        assistantMessage.Content.Should().Be(streamedText,
            "the persisted Content must match what was streamed, so the transcript and admin view show what the user saw");
        assistantMessage.Content.Should().NotBeNullOrEmpty();

        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("no assistant text")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [HumansFact]
    public async Task Ask_stores_a_one_liner_when_route_to_issue_produces_no_preamble_text()
    {
        // nobodies-collective/Humans#952 — route_to_issue's proposal frame is the terminal
        // output for the client, but a blank stored Content makes the admin transcript
        // view misleading when the model called the tool with no preamble.
        var userId = Guid.NewGuid();
        var dispatcher = Substitute.For<IAgentToolDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<AnthropicToolCall>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new AnthropicToolResult(
                call.Arg<AnthropicToolCall>().Id, "Proposal queued.", IsError: false)));
        var (svc, client) = await BuildService(s => s.Enabled = true, toolDispatcher: dispatcher);

        client.EnqueueTurn(
            new AgentTurnToken(null, new AnthropicToolCall(
                "tc1", AgentToolNames.RouteToIssue,
                """{"title":"Broken link","category":"Bug","description":"The camps page 404s."}"""), null),
            new AgentTurnToken(null, null, new AgentTurnFinalizer(0, 0, 0, 0, "claude-sonnet-4-6", "tool_use")));

        var tokens = new List<AgentTurnToken>();
        await foreach (var t in svc.AskAsync(
            new AgentTurnRequest(ConversationId: Guid.Empty, UserId: userId, Message: "the camps page is broken", Locale: "es"),
            Xunit.TestContext.Current.CancellationToken))
        {
            tokens.Add(t);
        }

        // No prose is streamed for a proposal-only turn: when nothing was streamed the
        // widget renders its own localized handoff line, and a streamed English delta
        // would override that localization for non-English users.
        tokens.Should().NotContain(t => t.TextDelta != null,
            "a proposal-only turn streams no prose so the widget's localized fallback applies");
        tokens.Should().Contain(t => t.IssueProposal != null);

        var finalizer = tokens.Last().Finalizer!;
        var transcript = await svc.GetConversationForUserAsync(
            userId, finalizer.ConversationId, Xunit.TestContext.Current.CancellationToken);
        var assistantMessage = transcript!.Messages.Single(m => m.Role == AgentRole.Assistant);
        assistantMessage.Content.Should().NotBeNullOrEmpty(
            "a blank stored Content makes the admin transcript view misleading");
    }

    [HumansFact]
    public async Task Ask_treats_whitespace_only_output_as_silent_and_yields_fallback()
    {
        // nobodies-collective/Humans#952 — whitespace-only deltas render as a blank
        // bubble and must receive the same fallback as truly zero-length output.
        var userId = Guid.NewGuid();
        var logger = Substitute.For<ILogger<AgentService>>();
        var (svc, client) = await BuildService(s => s.Enabled = true, logger: logger);

        client.EnqueueTurn(
            new AgentTurnToken(" \n\n ", null, null),
            new AgentTurnToken(null, null, new AgentTurnFinalizer(40, 5, 0, 0, "claude-sonnet-4-6", "end_turn")));

        var tokens = new List<AgentTurnToken>();
        await foreach (var t in svc.AskAsync(
            new AgentTurnRequest(ConversationId: Guid.Empty, UserId: userId, Message: "hello?", Locale: "en"),
            Xunit.TestContext.Current.CancellationToken))
        {
            tokens.Add(t);
        }

        var streamedText = string.Concat(tokens.Where(t => t.TextDelta != null).Select(t => t.TextDelta));
        streamedText.Trim().Should().NotBeNullOrEmpty(
            "whitespace-only model output must get the same fallback as zero-length output");

        var finalizer = tokens.Last().Finalizer!;
        var transcript = await svc.GetConversationForUserAsync(
            userId, finalizer.ConversationId, Xunit.TestContext.Current.CancellationToken);
        var assistantMessage = transcript!.Messages.Single(m => m.Role == AgentRole.Assistant);
        assistantMessage.Content.Trim().Should().NotBeNullOrEmpty();

        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("no assistant text")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [HumansFact]
    public async Task Ask_continues_the_tool_loop_when_a_tool_call_is_truncated_by_max_tokens()
    {
        // nobodies-collective/Humans#963 — a max_tokens cutoff mid tool-call JSON used to
        // discard the call outright: the loop only ever continued on StopReason=="tool_use".
        // AnthropicClient still closes the current content block before the stream ends, so a
        // (possibly malformed) AnthropicToolCall reaches AgentService even on a max_tokens stop.
        var userId = Guid.NewGuid();
        var dispatcher = Substitute.For<IAgentToolDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<AnthropicToolCall>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new AnthropicToolResult(
                call.Arg<AnthropicToolCall>().Id, "Malformed tool arguments (expected JSON object).", IsError: true)));
        var (svc, client) = await BuildService(s => s.Enabled = true, toolDispatcher: dispatcher);

        // Iteration 1: max_tokens hits mid tool-call JSON.
        client.EnqueueTurn(
            new AgentTurnToken("Let me check that. ", null, null),
            new AgentTurnToken(null, new AnthropicToolCall("t1", "fetch_section_guide", """{"section":"tea"""), null),
            new AgentTurnToken(null, null, new AgentTurnFinalizer(100, 20, 0, 0, "claude-sonnet-4-6", "max_tokens")));

        // Iteration 2: the follow-up call recovers and answers normally.
        client.EnqueueTurn(
            new AgentTurnToken("Teams are groups of volunteers.", null, null),
            new AgentTurnToken(null, null, new AgentTurnFinalizer(40, 10, 0, 0, "claude-sonnet-4-6", "end_turn")));

        var tokens = new List<AgentTurnToken>();
        await foreach (var t in svc.AskAsync(
            new AgentTurnRequest(ConversationId: Guid.Empty, UserId: userId, Message: "What are teams?", Locale: "es"),
            Xunit.TestContext.Current.CancellationToken))
        {
            tokens.Add(t);
        }

        await dispatcher.Received(1).DispatchAsync(
            Arg.Any<AnthropicToolCall>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());

        var streamedText = string.Concat(tokens.Where(t => t.TextDelta != null).Select(t => t.TextDelta));
        streamedText.Should().Contain("Teams are groups of volunteers",
            "a max_tokens cutoff mid tool-call must not dead-end the turn — the loop retries and the model finishes its answer");

        tokens.Last().Finalizer!.StopReason.Should().Be("end_turn");

        // Dispatching the truncated call must not inflate the admin "Top fetched docs" panel:
        // NormalizeFetchedDocSlug can't parse the truncated args, so it would fall back to the
        // bare tool name and record a lookup that never returned a document.
        var rows = await svc.ListAllConversationsForAdminWithMessagesAsync(
            refusalsOnly: false, handoffsOnly: false, userId: userId, take: 50, skip: 0,
            Xunit.TestContext.Current.CancellationToken);
        rows.SelectMany(r => r.Messages).SelectMany(m => m.FetchedDocs)
            .Should().BeEmpty("a failed tool dispatch is not a fetched doc");
    }

    [HumansFact]
    public async Task Ask_persists_an_assistant_message_when_an_exception_escapes_the_turn()
    {
        // nobodies-collective/Humans#963 — 4 of 11 conversations in #952's log evidence never
        // reached AppendMessageAsync for the assistant turn because an exception escaped the
        // stream between the user message being written and the assistant message being
        // written. A failed turn must still leave a trace in the transcript.
        var userId = Guid.NewGuid();
        var dispatcher = Substitute.For<IAgentToolDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<AnthropicToolCall>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task<AnthropicToolResult>>(_ => throw new InvalidOperationException("simulated dispatch failure"));
        var logger = Substitute.For<ILogger<AgentService>>();
        var store = new AgentRateLimitStore();
        var (svc, client) = await BuildService(s => s.Enabled = true, rateLimitStore: store,
            toolDispatcher: dispatcher, logger: logger);

        client.EnqueueTurn(
            new AgentTurnToken(null, new AnthropicToolCall("t1", "fetch_section_guide", """{"section":"teams"}"""), null),
            new AgentTurnToken(null, null, new AgentTurnFinalizer(100, 20, 0, 0, "claude-sonnet-4-6", "tool_use")));

        var tokens = new List<AgentTurnToken>();
        await foreach (var t in svc.AskAsync(
            new AgentTurnRequest(ConversationId: Guid.Empty, UserId: userId, Message: "What are teams?", Locale: "es"),
            Xunit.TestContext.Current.CancellationToken))
        {
            tokens.Add(t);
        }

        var finalizer = tokens.Last().Finalizer;
        finalizer.Should().NotBeNull();
        finalizer!.StopReason.Should().Be("error");
        finalizer.ConversationId.Should().NotBe(Guid.Empty,
            "the client must be able to continue the same conversation after a failed turn");
        // nobodies-collective/Humans#990: the streamed error frame must agree with what got
        // persisted and billed below, not hardcode zeros — that was a contract inconsistency
        // between three surfaces (SSE finalizer, AgentMessage row, rate-limit billing).
        finalizer.InputTokens.Should().Be(100);
        finalizer.OutputTokens.Should().Be(20);

        var transcript = await svc.GetConversationForUserAsync(
            userId, finalizer.ConversationId, Xunit.TestContext.Current.CancellationToken);
        transcript.Should().NotBeNull();
        var failureMessage = transcript!.Messages.Should().ContainSingle(m => m.Role == AgentRole.Assistant,
            "a turn that throws mid-stream must still leave an assistant message in the transcript").Subject;
        failureMessage.RefusalReason.Should().Be("error");
        // AgentAdminStatusService prices spend straight off these fields, so a zeroed trace
        // would hide a turn the provider actually billed us for.
        failureMessage.PromptTokens.Should().Be(100);
        failureMessage.OutputTokens.Should().Be(20);

        logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Is<Exception>(e => e is InvalidOperationException),
            Arg.Any<Func<object, Exception?, string>>());

        // A failed turn is still a billed turn: the provider charged us for the 100/20 the
        // first request consumed before the dispatcher threw, and leaving the message cap
        // untouched would make a deterministic backend error an unmetered send loop.
        var usage = store.Get(userId, new LocalDate(2026, 4, 21), hour: 12);
        usage.MessagesToday.Should().Be(1);
        usage.MessagesThisHour.Should().Be(1);
        usage.TokensToday.Should().Be(120,
            "tokens the provider already billed before the failure must reach DailyTokenCap and admin spend");
    }

    [HumansFact]
    public async Task Ask_writes_one_trace_when_the_assistant_append_itself_is_cancelled()
    {
        // AgentRepository shares one scoped AgentDbContext across the turn. A cancellation that
        // lands inside the final AppendMessageAsync leaves the assistant message tracked as
        // Added, so the failure trace's save would flush BOTH — a real reply plus an "error"
        // refusal for the same turn, and a double MessageCount bump. The repository discards the
        // half-written append on failure so the turn ends with exactly one trace.
        var userId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();

        // route_to_issue is the one turn shape that reaches the final append with no further
        // provider call in between (the proposal frame is terminal), so cancelling from the
        // dispatcher lands the cancellation inside AppendMessageAsync itself rather than in
        // the streaming loop — which is the ordering the duplicate-write hazard needs.
        var dispatcher = Substitute.For<IAgentToolDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<AnthropicToolCall>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await cts.CancelAsync();
                return new AnthropicToolResult(
                    call.Arg<AnthropicToolCall>().Id, "Proposal queued.", IsError: false);
            });
        var (svc, client) = await BuildService(s => s.Enabled = true, toolDispatcher: dispatcher);

        client.EnqueueTurn(
            new AgentTurnToken(null, new AnthropicToolCall(
                "tc1", AgentToolNames.RouteToIssue,
                """{"title":"Broken link","category":"Bug","description":"The camps page 404s."}"""), null),
            new AgentTurnToken(null, null, new AgentTurnFinalizer(40, 10, 0, 0, "claude-sonnet-4-6", "tool_use")));

        await foreach (var _ in svc.AskAsync(
            new AgentTurnRequest(ConversationId: Guid.Empty, UserId: userId, Message: "the camps page is broken", Locale: "es"),
            cts.Token))
        {
            // Drain; the assertions below read the persisted transcript.
        }

        var rows = await svc.ListAllConversationsForAdminWithMessagesAsync(
            refusalsOnly: false, handoffsOnly: false, userId: userId, take: 50, skip: 0,
            Xunit.TestContext.Current.CancellationToken);
        var conversation = rows.Should().ContainSingle().Subject;
        var assistant = conversation.Messages.Should()
            .ContainSingle(m => m.Role == AgentRole.Assistant,
                "the cancelled append must not be flushed alongside the failure trace")
            .Subject;
        assistant.RefusalReason.Should().Be("error");
        assistant.Content.Should().BeEmpty();
        conversation.MessageCount.Should().Be(2, "one user message and one assistant trace");
    }

    [HumansFact]
    public async Task Ask_persists_an_assistant_message_when_the_stream_is_abandoned_mid_turn()
    {
        // nobodies-collective/Humans#963 — AgentController awaits WriteSse OUTSIDE AskAsync, so
        // a browser that goes away while a token is being written never throws into AskAsync's
        // catch: it abandons the enumeration, which disposes the iterator at a `yield return`.
        // That is the likeliest disconnect timing, and it must still leave the already-persisted
        // user message with a matching assistant trace.
        var userId = Guid.NewGuid();
        var (svc, client) = await BuildService(s => s.Enabled = true);

        client.EnqueueTurn(
            new AgentTurnToken("Teams are groups of ", null, null),
            new AgentTurnToken("volunteers.", null, null),
            new AgentTurnToken(null, null, new AgentTurnFinalizer(40, 10, 0, 0, "claude-sonnet-4-6", "end_turn")));

        // `break` disposes the async enumerator — exactly what the controller's `await foreach`
        // does when the response write throws on a dead connection.
        await foreach (var t in svc.AskAsync(
            new AgentTurnRequest(ConversationId: Guid.Empty, UserId: userId, Message: "What are teams?", Locale: "es"),
            Xunit.TestContext.Current.CancellationToken))
        {
            if (t.TextDelta is not null)
                break;
        }

        var rows = await svc.ListAllConversationsForAdminWithMessagesAsync(
            refusalsOnly: false, handoffsOnly: false, userId: userId, take: 50, skip: 0,
            Xunit.TestContext.Current.CancellationToken);
        var messages = rows.SelectMany(r => r.Messages).ToList();
        messages.Should().Contain(m => m.Role == AgentRole.User,
            "the user message is persisted before the turn starts streaming");
        var failure = messages.Should().ContainSingle(m => m.Role == AgentRole.Assistant,
            "an abandoned turn must leave exactly one assistant trace — no reply, and no duplicate")
            .Subject;
        failure.RefusalReason.Should().Be("error",
            "the trace surfaces through the existing admin refusals filter");
    }

    [HumansFact]
    public async Task Ask_does_not_add_a_failure_trace_when_the_consumer_stops_after_the_finalizer()
    {
        // The disconnect guard above must not fire on a turn that completed: RunTurnAsync yields
        // its finalizer only after AppendMessageAsync, so a disconnect while writing that last
        // frame would otherwise stack a bogus "error" trace on top of a perfectly good answer.
        var userId = Guid.NewGuid();
        var (svc, client) = await BuildService(s => s.Enabled = true);

        client.EnqueueTurn(
            new AgentTurnToken("Teams are groups of volunteers.", null, null),
            new AgentTurnToken(null, null, new AgentTurnFinalizer(40, 10, 0, 0, "claude-sonnet-4-6", "end_turn")));

        await foreach (var t in svc.AskAsync(
            new AgentTurnRequest(ConversationId: Guid.Empty, UserId: userId, Message: "What are teams?", Locale: "es"),
            Xunit.TestContext.Current.CancellationToken))
        {
            if (t.Finalizer is not null)
                break;
        }

        var rows = await svc.ListAllConversationsForAdminWithMessagesAsync(
            refusalsOnly: false, handoffsOnly: false, userId: userId, take: 50, skip: 0,
            Xunit.TestContext.Current.CancellationToken);
        var assistant = rows.SelectMany(r => r.Messages).Should()
            .ContainSingle(m => m.Role == AgentRole.Assistant).Subject;
        assistant.Content.Should().Be("Teams are groups of volunteers.");
        assistant.RefusalReason.Should().BeNull();
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
        IAgentToolDispatcher? toolDispatcher = null,
        ILogger<AgentService>? logger = null)
    {
        var dbOptions = new DbContextOptionsBuilder<AgentDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AgentDbContext(dbOptions);

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
        var resolvedLogger = logger ?? NullLogger<AgentService>.Instance;

        var svc = new AgentService(settingsService, ratelimit, abuse, repo, new AgentRetentionRunStore(),
            snapshots, preload, assembler, tools, client, options, clock, resolvedLogger);
        return (svc, client);
    }

    private sealed class FakeClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }
}

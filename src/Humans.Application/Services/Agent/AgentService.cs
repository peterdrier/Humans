using System.Runtime.CompilerServices;
using System.Text;
using Humans.Application.Configuration;
using Humans.Application.Constants;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Stores;
using Humans.Application.Models;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Application.Services.Agent;

public sealed class AgentService : IAgentService, IUserDataContributor
{
    private readonly IAgentSettingsService _settings;
    private readonly IAgentRateLimitStore _rateLimit;
    private readonly IAgentAbuseDetector _abuse;
    private readonly IAgentRepository _repo;
    private readonly IAgentUserSnapshotProvider _snapshots;
    private readonly IAgentPreloadCorpusBuilder _preload;
    private readonly IAgentPromptAssembler _assembler;
    private readonly IAgentToolDispatcher _tools;
    private readonly IAnthropicClient _client;
    private readonly AnthropicOptions _anthropicOptions;
    private readonly IClock _clock;
    private readonly ILogger<AgentService> _logger;

    public AgentService(
        IAgentSettingsService settings,
        IAgentRateLimitStore rateLimit,
        IAgentAbuseDetector abuse,
        IAgentRepository repo,
        IAgentUserSnapshotProvider snapshots,
        IAgentPreloadCorpusBuilder preload,
        IAgentPromptAssembler assembler,
        IAgentToolDispatcher tools,
        IAnthropicClient client,
        IOptions<AnthropicOptions> anthropicOptions,
        IClock clock,
        ILogger<AgentService> logger)
    {
        _settings = settings; _rateLimit = rateLimit; _abuse = abuse;
        _repo = repo; _snapshots = snapshots; _preload = preload;
        _assembler = assembler; _tools = tools; _client = client;
        _anthropicOptions = anthropicOptions.Value;
        _clock = clock; _logger = logger;
    }

    public async IAsyncEnumerable<AgentTurnToken> AskAsync(
        AgentTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        if (!settings.Enabled)
        {
            yield return Finalizer(stopReason: "disabled");
            yield break;
        }

        var now = _clock.GetCurrentInstant();
        var nowZoned = now.InUtc();
        var today = nowZoned.Date;
        var hour = nowZoned.Hour;
        var usage = _rateLimit.Get(request.UserId, today, hour);
        if (usage.MessagesToday >= settings.DailyMessageCap ||
            usage.TokensToday >= settings.DailyTokenCap ||
            usage.MessagesThisHour >= settings.HourlyMessageCap)
        {
            // Invariant 6 (Agent.md): every refused turn writes an AgentMessage
            // with RefusalReason != null. The controller-level handler normally
            // blocks first, but this in-service guard is a second line of
            // defence and must honour the same invariant.
            await PersistRefusal(request, "rate_limited", cancellationToken);
            yield return Finalizer(stopReason: "rate_limited");
            yield break;
        }

        if (_abuse.IsFlagged(request.Message, out var abuseReason))
        {
            await PersistRefusal(request, abuseReason!, cancellationToken);
            yield return new AgentTurnToken("This isn't something I can help with. If you're in distress, please contact a coordinator or emergency services.", null, null);
            yield return Finalizer(stopReason: "abuse_flag");
            yield break;
        }

        AgentConversation conversation;
        if (request.ConversationId == Guid.Empty)
        {
            conversation = await _repo.CreateConversationAsync(request.UserId, request.Locale, cancellationToken);
        }
        else
        {
            var existing = await _repo.GetConversationByIdAsync(request.ConversationId, cancellationToken);
            // Conversation may have been retention-purged or deleted in another tab
            // between page load and submit. Start a fresh one rather than 500ing
            // the SSE stream — the finalizer stamps the new ConversationId so the
            // client picks it up transparently.
            conversation = existing
                ?? await _repo.CreateConversationAsync(request.UserId, request.Locale, cancellationToken);
        }

        if (conversation.UserId != request.UserId)
            throw new UnauthorizedAccessException("Conversation does not belong to this user.");

        // Replay prior turns so the model has continuity. Snapshot before appending
        // the new user message so we don't double-add it to sdkMessages below. Only
        // user/assistant text turns replay; tool-call internals from prior turns are
        // dropped (the model re-derives them via fetch_section_guide as needed).
        var priorTurns = conversation.Messages
            .Where(m => (m.Role == AgentRole.User || m.Role == AgentRole.Assistant)
                        && !string.IsNullOrEmpty(m.Content))
            .OrderBy(m => m.CreatedAt)
            .TakeLast(HistoryReplayLimit)
            .ToList();

        await _repo.AppendMessageAsync(new AgentMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Role = AgentRole.User,
            Content = request.Message,
            CreatedAt = now,
            Model = settings.Model
        }, cancellationToken);

        var snapshot = await _snapshots.LoadAsync(request.UserId, cancellationToken);
        var preloadText = await _preload.BuildAsync(settings.PreloadConfig, cancellationToken);
        var systemPrompt = _assembler.BuildSystemPrompt(preloadText);
        var tail = _assembler.BuildUserContextTail(snapshot);
        var tools = _assembler.BuildToolDefinitions();

        var sdkMessages = new List<AnthropicMessage>(priorTurns.Count + 1);
        foreach (var prior in priorTurns)
        {
            sdkMessages.Add(new AnthropicMessage(
                Role: prior.Role == AgentRole.User ? "user" : "assistant",
                Text: prior.Content,
                ToolCalls: null,
                ToolResults: null));
        }
        sdkMessages.Add(new AnthropicMessage(
            Role: "user",
            Text: tail + "\n\n" + request.Message,
            ToolCalls: null,
            ToolResults: null));

        var assistantBuffer = new StringBuilder();
        var fetchedDocs = new List<string>();
        var toolCallCount = 0;
        Guid? handoffId = null;
        AgentTurnFinalizer? finalFinalizer = null;

        while (true)
        {
            var iterationAssistantText = new StringBuilder();
            var pendingToolCalls = new List<AnthropicToolCall>();

            await foreach (var token in _client.StreamAsync(
                new AnthropicRequest(settings.Model, systemPrompt, sdkMessages, tools, MaxOutputTokens: 1024),
                cancellationToken))
            {
                if (token.TextDelta is { Length: > 0 } delta)
                {
                    iterationAssistantText.Append(delta);
                    assistantBuffer.Append(delta);
                    yield return new AgentTurnToken(delta, null, null);
                }
                else if (token.ToolCall is { } call)
                {
                    pendingToolCalls.Add(call);
                }
                else if (token.Finalizer is { } f)
                {
                    finalFinalizer = f;
                }
            }

            if (pendingToolCalls.Count == 0 || !string.Equals(finalFinalizer?.StopReason, "tool_use", StringComparison.Ordinal))
                break;

            sdkMessages.Add(new AnthropicMessage(
                Role: "assistant",
                Text: iterationAssistantText.Length > 0 ? iterationAssistantText.ToString() : null,
                ToolCalls: pendingToolCalls,
                ToolResults: null));

            var results = new List<AnthropicToolResult>();
            foreach (var call in pendingToolCalls)
            {
                toolCallCount++;
                if (toolCallCount > _anthropicOptions.MaxToolCallsPerTurn)
                {
                    results.Add(new AnthropicToolResult(call.Id,
                        "Too many lookups. Try a narrower question.", IsError: true));
                    break;
                }

                var result = await _tools.DispatchAsync(call, request.UserId, conversation.Id, cancellationToken);
                results.Add(result);
                fetchedDocs.Add(call.Name + ":" + call.JsonArguments);

                if (string.Equals(call.Name, AgentToolNames.RouteToFeedback, StringComparison.Ordinal) && !result.IsError)
                {
                    var marker = "/Feedback/";
                    var idx = result.Content.IndexOf(marker, StringComparison.Ordinal);
                    if (idx >= 0 && Guid.TryParse(result.Content.AsSpan(idx + marker.Length), out var id))
                        handoffId = id;
                }
            }

            sdkMessages.Add(new AnthropicMessage("tool", Text: null, ToolCalls: null, ToolResults: results));

            if (handoffId is not null || toolCallCount >= _anthropicOptions.MaxToolCallsPerTurn)
                break;
        }

        var message = new AgentMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Role = AgentRole.Assistant,
            Content = assistantBuffer.ToString(),
            CreatedAt = _clock.GetCurrentInstant(),
            PromptTokens = finalFinalizer?.InputTokens ?? 0,
            OutputTokens = finalFinalizer?.OutputTokens ?? 0,
            CachedTokens = finalFinalizer?.CacheReadTokens ?? 0,
            Model = settings.Model,
            DurationMs = 0,
            FetchedDocs = fetchedDocs.ToArray(),
            HandedOffToFeedbackId = handoffId
        };
        await _repo.AppendMessageAsync(message, cancellationToken);

        var totalTokens = message.PromptTokens + message.OutputTokens;
        _rateLimit.Record(request.UserId, today, hour, messagesDelta: 1, tokensDelta: totalTokens);

        var fallbackFinalizer = finalFinalizer ?? new AgentTurnFinalizer(0, 0, 0, 0, _settings.Current.Model, "unknown");
        // Stamp the conversation id so the client can reuse it on the next send.
        yield return new AgentTurnToken(null, null, fallbackFinalizer with { ConversationId = conversation.Id });
    }

    /// <summary>How many prior user/assistant turns to replay to the model. Bounded
    /// so long conversations don't blow the context budget; the daily message cap
    /// (default 30) keeps most conversations well under this.</summary>
    private const int HistoryReplayLimit = 20;

    public Task<IReadOnlyList<AgentConversation>> GetHistoryAsync(Guid userId, int take, CancellationToken ct) =>
        _repo.ListConversationsForUserAsync(userId, take, ct);

    public async Task DeleteConversationAsync(Guid userId, Guid conversationId, CancellationToken ct)
    {
        var conv = await _repo.GetConversationByIdAsync(conversationId, ct);
        if (conv is null || conv.UserId != userId) return;
        await _repo.DeleteConversationAsync(conversationId, ct);
    }

    public Task<IReadOnlyList<AgentConversation>> ListAllConversationsForAdminAsync(
        bool refusalsOnly, bool handoffsOnly, Guid? userId, int take, int skip,
        CancellationToken ct) =>
        _repo.ListAllConversationsAsync(refusalsOnly, handoffsOnly, userId, take, skip, ct);

    public Task<AgentConversation?> GetConversationForAdminAsync(Guid id, CancellationToken ct) =>
        _repo.GetConversationByIdAsync(id, ct);

    public async Task<AgentPromptPreview?> GetPromptPreviewForAdminAsync(
        Guid conversationId, CancellationToken ct)
    {
        var conversation = await _repo.GetConversationByIdAsync(conversationId, ct);
        if (conversation is null) return null;

        var settings = _settings.Current;
        var snapshot = await _snapshots.LoadAsync(conversation.UserId, ct);
        var preloadText = await _preload.BuildAsync(settings.PreloadConfig, ct);
        var systemPrompt = _assembler.BuildSystemPrompt(preloadText);
        var tail = _assembler.BuildUserContextTail(snapshot);
        var toolDefs = _assembler.BuildToolDefinitions();

        // Mirror AskAsync's history replay rules so the preview matches what
        // would actually be sent on the next turn.
        var replayed = conversation.Messages
            .Where(m => (m.Role == AgentRole.User || m.Role == AgentRole.Assistant)
                        && !string.IsNullOrEmpty(m.Content))
            .OrderBy(m => m.CreatedAt)
            .TakeLast(HistoryReplayLimit)
            .Select(m => new AgentPromptHistoryTurn(
                Role: m.Role == AgentRole.User ? "user" : "assistant",
                Text: m.Content))
            .ToList();

        var tools = toolDefs
            .Select(t => new AgentPromptToolDefinition(t.Name, t.Description, t.JsonSchema))
            .ToList();

        return new AgentPromptPreview(
            Model: settings.Model,
            SystemPrompt: systemPrompt,
            UserContextTail: tail,
            Tools: tools,
            ReplayedHistory: replayed);
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var conversations = await _repo.ListConversationsForUserWithMessagesAsync(userId, ct);
        var shaped = conversations.Select(c => new
        {
            c.Id,
            c.StartedAt,
            c.LastMessageAt,
            c.Locale,
            c.MessageCount,
            Messages = c.Messages.Select(m => new
            {
                m.Role,
                m.Content,
                m.CreatedAt,
                m.Model,
                m.RefusalReason,
                m.HandedOffToFeedbackId
            }).ToList()
        }).ToList();
        return [new UserDataSlice(GdprExportSections.AgentConversations, shaped)];
    }

    private AgentTurnToken Finalizer(string stopReason) =>
        new(null, null, new AgentTurnFinalizer(0, 0, 0, 0, _settings.Current.Model, stopReason));

    private async Task PersistRefusal(AgentTurnRequest req, string reason, CancellationToken ct)
    {
        AgentConversation conv;
        if (req.ConversationId == Guid.Empty)
        {
            conv = await _repo.CreateConversationAsync(req.UserId, req.Locale, ct);
        }
        else
        {
            var existing = await _repo.GetConversationByIdAsync(req.ConversationId, ct);
            // Refusal must be persisted (Agent.md invariant 6), but never into
            // someone else's transcript. The rate-limit/abuse paths in AskAsync
            // run BEFORE the ownership check, so a client supplying another
            // user's conversation GUID would otherwise pollute their thread.
            conv = (existing is not null && existing.UserId == req.UserId)
                ? existing
                : await _repo.CreateConversationAsync(req.UserId, req.Locale, ct);
        }

        await _repo.AppendMessageAsync(new AgentMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conv.Id,
            Role = AgentRole.Assistant,
            Content = "",
            CreatedAt = _clock.GetCurrentInstant(),
            Model = _settings.Current.Model,
            RefusalReason = reason
        }, ct);
    }
}

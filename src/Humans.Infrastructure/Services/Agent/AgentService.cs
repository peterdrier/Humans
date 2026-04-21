using System.Runtime.CompilerServices;
using System.Text;
using Humans.Application.Constants;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Stores;
using Humans.Application.Models;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Infrastructure.Services.Agent;

public sealed class AgentService : IAgentService
{
    private readonly IAgentSettingsService _settings;
    private readonly IAgentRateLimitStore _rateLimit;
    private readonly IAgentAbuseDetector _abuse;
    private readonly IAgentConversationRepository _repo;
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
        IAgentConversationRepository repo,
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
        var today = now.InUtc().Date;
        var usage = _rateLimit.Get(request.UserId, today);
        if (usage.MessagesToday >= settings.DailyMessageCap ||
            usage.TokensToday >= settings.DailyTokenCap)
        {
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

        var conversation = request.ConversationId == Guid.Empty
            ? await _repo.CreateAsync(request.UserId, request.Locale, cancellationToken)
            : await _repo.GetByIdAsync(request.ConversationId, cancellationToken)
              ?? throw new InvalidOperationException("Unknown conversation");

        if (conversation.UserId != request.UserId)
            throw new UnauthorizedAccessException("Conversation does not belong to this user.");

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

        var sdkMessages = new List<AnthropicMessage>
        {
            new(Role: "user", Text: tail + "\n\n" + request.Message, ToolCalls: null, ToolResults: null)
        };

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

            // FIX 3 part 1 — string.Equals with StringComparison.Ordinal
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

                // FIX 3 part 2 — string.Equals with StringComparison.Ordinal
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
        _rateLimit.Record(request.UserId, today, messagesDelta: 1, tokensDelta: totalTokens);

        // FIX 2 — break null-coalesce apart to avoid type mismatch
        var fallbackFinalizer = finalFinalizer ?? new AgentTurnFinalizer(0, 0, 0, 0, _settings.Current.Model, "unknown");
        yield return new AgentTurnToken(null, null, fallbackFinalizer);
    }

    public Task<IReadOnlyList<AgentConversation>> GetHistoryAsync(Guid userId, int take, CancellationToken ct) =>
        _repo.ListForUserAsync(userId, take, ct);

    public async Task DeleteConversationAsync(Guid userId, Guid conversationId, CancellationToken ct)
    {
        var conv = await _repo.GetByIdAsync(conversationId, ct);
        if (conv is null || conv.UserId != userId) return;
        await _repo.DeleteAsync(conversationId, ct);
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var conversations = await _repo.ListForUserAsync(userId, take: int.MaxValue, ct);
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

    // FIX 1 — helper returns AgentTurnToken (wrapping the finalizer), not AgentTurnFinalizer directly
    private AgentTurnToken Finalizer(string stopReason) =>
        new(null, null, new AgentTurnFinalizer(0, 0, 0, 0, _settings.Current.Model, stopReason));

    private async Task PersistRefusal(AgentTurnRequest req, string reason, CancellationToken ct)
    {
        var conv = req.ConversationId == Guid.Empty
            ? await _repo.CreateAsync(req.UserId, req.Locale, ct)
            : await _repo.GetByIdAsync(req.ConversationId, ct) ?? await _repo.CreateAsync(req.UserId, req.Locale, ct);

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

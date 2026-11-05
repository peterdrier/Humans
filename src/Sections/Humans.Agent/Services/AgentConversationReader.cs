using Humans.Agent.Contracts;
using Humans.Agent.Services.Anthropic;
using Humans.Agent.Data;
using Humans.Agent.Domain;
using Humans.Agent.Models;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;

namespace Humans.Agent.Services;

internal sealed class AgentConversationReader(
    IAgentRepository repo,
    IAgentUserSnapshotProvider snapshots,
    IAgentPromptAssembler assembler,
    IUserService users,
    IAgentSettingsService settings,
    IAgentPreloadCorpusBuilder preload,
    IAnthropicClient client,
    ILogger<AgentService> logger) : IApplicationService
{
    internal const string AgentConversations = "AgentConversations";

    public async Task<IReadOnlyList<AgentConversationListSnapshot>> GetHistoryAsync(
        Guid userId, int take, CancellationToken ct)
    {
        var conversations = await repo.ListConversationsForUserAsync(userId, take, ct);
        return conversations.Select(ToListSnapshot).ToList();
    }

    public async Task<AgentConversationTranscriptSnapshot?> GetConversationForUserAsync(
        Guid userId, Guid conversationId, CancellationToken ct)
    {
        var conv = await repo.GetConversationByIdAsync(conversationId, ct);
        return conv is not null && conv.UserId == userId ? ToTranscriptSnapshot(conv) : null;
    }

    public async Task<AgentMyConversationView?> GetMyConversationAsync(
        Guid userId, Guid conversationId, CancellationToken ct)
    {
        var conv = await repo.GetConversationByIdAsync(conversationId, ct);
        // Ownership mismatch returns null like "not found" (Agent.md invariant 7) — no existence leak.
        if (conv is null || conv.UserId != userId) return null;

        // Tail regenerated from current snapshot; may differ from historical turn view (Agent.md open question).
        var snapshot = await snapshots.LoadAsync(userId, ct);
        var tail = assembler.BuildUserContextTail(snapshot);
        return new AgentMyConversationView(ToTranscriptSnapshot(conv), tail);
    }

    public async Task<IReadOnlyList<AgentConversationListSnapshot>> ListAllConversationsForAdminAsync(
        bool refusalsOnly, Guid? userId, int take, int skip,
        CancellationToken ct)
    {
        var conversations = await repo.ListAllConversationsAsync(refusalsOnly, userId, take, skip, ct);
        return conversations.Select(ToListSnapshot).ToList();
    }

    public async Task<IReadOnlyList<AgentConversationTranscriptSnapshot>> ListAllConversationsForAdminWithMessagesAsync(
        bool refusalsOnly, bool handoffsOnly, Guid? userId, int take, int skip,
        CancellationToken ct)
    {
        var conversations = await repo.ListAllConversationsWithMessagesAsync(
            refusalsOnly, handoffsOnly, userId, take, skip, ct);
        return conversations.Select(ToTranscriptSnapshot).ToList();
    }

    private static AgentConversationTranscriptSnapshot ToTranscriptSnapshot(AgentConversation conversation) =>
        new(
            conversation.Id,
            conversation.UserId,
            conversation.Locale,
            conversation.StartedAt,
            conversation.LastMessageAt,
            conversation.MessageCount,
            conversation.Messages
                .Select(message => new AgentMessageSnapshot(
                    message.Id,
                    message.ConversationId,
                    message.Role,
                    message.Content,
                    message.CreatedAt,
                    message.PromptTokens,
                    message.OutputTokens,
                    message.CachedTokens,
                    message.Model,
                    message.DurationMs,
                    message.FetchedDocs,
                    message.RefusalReason,
                    message.HandedOffToFeedbackId))
                .ToList());

    private static AgentConversationListSnapshot ToListSnapshot(AgentConversation conversation) =>
        new(
            conversation.Id,
            conversation.UserId,
            conversation.Locale,
            conversation.StartedAt,
            conversation.LastMessageAt,
            conversation.MessageCount);

    public async Task<AgentConversationTranscriptSnapshot?> GetConversationForAdminAsync(Guid id, CancellationToken ct)
    {
        var conversation = await repo.GetConversationByIdAsync(id, ct);
        return conversation is null ? null : ToTranscriptSnapshot(conversation);
    }

    public async Task<AgentPromptPreview?> GetPromptPreviewForAdminAsync(
        Guid conversationId, CancellationToken ct)
    {
        var conversation = await repo.GetConversationByIdAsync(conversationId, ct);
        if (conversation is null) return null;

        var currentSettings = settings.Current;
        var snapshot = await snapshots.LoadAsync(conversation.UserId, ct);
        var preloadText = await preload.BuildAsync(currentSettings.PreloadConfig, ct);
        var systemPrompt = assembler.BuildSystemPrompt(preloadText);
        var tail = assembler.BuildUserContextTail(snapshot);
        var toolDefs = assembler.BuildToolDefinitions();

        // Mirror AskAsync's history replay rules so the preview matches what
        // would actually be sent on the next turn.
        var replayed = conversation.GetReplayableMessages()
            .Select(m => new AgentPromptHistoryTurn(
                Role: m.Role == AgentRole.User ? "user" : "assistant",
                Text: m.Content))
            .ToList();

        var tools = toolDefs
            .Select(t => new AgentPromptToolDefinition(t.Name, t.Description, t.JsonSchema))
            .ToList();

        // Measure the system prompt with the real tokenizer (count_tokens). This is a diagnostic
        // nicety — a failed/rate-limited count renders as null; request cancellation propagates.
        int? systemPromptTokens = null;
        try
        {
            systemPromptTokens = await client.CountTokensAsync(currentSettings.Model, systemPrompt, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Expected/transient (rate limit, network) — log the reason at Warning, drop the
            // stack trace per memory/code/always-log-problems.md.
            logger.LogWarning(
                "count_tokens failed for prompt preview; rendering without a token count: {Reason}", ex.Message);
        }

        return new AgentPromptPreview(
            Model: currentSettings.Model,
            SystemPrompt: systemPrompt,
            UserContextTail: tail,
            Tools: tools,
            ReplayedHistory: replayed,
            SystemPromptTokens: systemPromptTokens);
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        var conversations = new List<AgentConversation>();
        foreach (var id in info?.AllUserIds ?? [userId])
            conversations.AddRange(await repo.ListConversationsForUserWithMessagesAsync(id, ct));
        var shaped = conversations.OrderBy(c => c.StartedAt).Select(c => new
        {
            c.Id,
            StartedAt = c.StartedAt.ToIso8601(),
            LastMessageAt = c.LastMessageAt.ToIso8601(),
            c.Locale,
            c.MessageCount,
            Messages = c.Messages.Select(m => new
            {
                m.Role,
                m.Content,
                CreatedAt = m.CreatedAt.ToIso8601(),
                m.Model,
                m.RefusalReason,
                m.HandedOffToFeedbackId
            }).ToList()
        }).ToList();
        return [new UserDataSlice(AgentConversations, shaped)];
    }
}

using System.Runtime.CompilerServices;
using System.Text;
using Humans.Application.Configuration;
using Humans.Application.Constants;
using Humans.Application.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Agent.Data;
using Humans.Agent.Services.Stores;
using Humans.Application.Models;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Agent.Domain;
using Humans.Agent.Models;
using Humans.Agent.Contracts;
using Humans.Issues.Contracts;
using Humans.Agent.Services.Anthropic;

namespace Humans.Agent.Services;

internal sealed class AgentService : IAgentService, IAgentConversationRetention
{
    private readonly IAgentSettingsService _settings;
    private readonly IAgentRateLimitStore _rateLimit;
    private readonly IAgentAbuseDetector _abuse;
    private readonly IAgentRepository _repo;
    private readonly IAgentRetentionRunStore _retentionRuns;
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
        IAgentRetentionRunStore retentionRuns,
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
        _repo = repo; _retentionRuns = retentionRuns; _snapshots = snapshots; _preload = preload;
        _assembler = assembler; _tools = tools; _client = client;
        _anthropicOptions = anthropicOptions.Value;
        _clock = clock; _logger = logger;
    }

    /// <summary>
    /// Deletes conversations past the configured retention window and records the run.
    /// Called by <c>AgentConversationRetentionJob</c>, which stays in Base (design §15.6b);
    /// the window, the purge and the last-run record all belong here.
    /// </summary>
    public async Task<int> PurgeExpiredConversationsAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetCurrentInstant();
        var cutoff = now - Duration.FromDays(_settings.Current.RetentionDays);
        var deleted = await _repo.PurgeConversationsOlderThanAsync(cutoff, cancellationToken);

        // Always record the run — the admin status panel needs the timestamp even when nothing
        // was deleted, so an operator can confirm the job is alive. Recording happens after the
        // purge so a thrown exception surfaces as "last run was earlier" rather than a
        // misleading green tick.
        _retentionRuns.Record(now, deleted);
        return deleted;
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
            // Invariant 6 (Agent.md): every refused turn writes an AgentMessage with RefusalReason.
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
            // Conversation may have been retention-purged; fall back to a fresh one (finalizer stamps the new id).
            conversation = existing
                ?? await _repo.CreateConversationAsync(request.UserId, request.Locale, cancellationToken);
        }

        if (conversation.UserId != request.UserId)
            throw new UnauthorizedAccessException("Conversation does not belong to this user.");

        // Replay user/assistant text turns only — tool-call internals are dropped (model re-derives via fetch_section_guide).
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

        // From here on, a thrown exception (or client disconnect) would otherwise leave the
        // user message above with no matching assistant reply (nobodies-collective/Humans#963:
        // 4 of 11 conversations in #952's log evidence never reached the AppendMessageAsync
        // below because something threw between the two writes). `await foreach` forbids
        // `yield` inside its implicit try/finally, so drive the inner enumerator manually —
        // that lets MoveNextAsync be wrapped in try/catch while still streaming tokens live.
        // Shared with RunTurnAsync so the failure path below can still bill a turn that broke
        // partway: the running totals live inside the iterator, out of reach of this method.
        var turnUsage = new TurnUsage();
        var turn = RunTurnAsync(request, conversation, settings, priorTurns, today, hour, turnUsage, cancellationToken);
        await using var enumerator = turn.GetAsyncEnumerator(cancellationToken);
        // False until the assistant message for this turn is on disk — written either by
        // RunTurnAsync itself (it yields its finalizer only after AppendMessageAsync) or by
        // the finally below. While it's false the persisted user message still owes the user
        // a reply, and the turn is still unbilled.
        var assistantPersisted = false;
        Exception? turnFailure = null;
        try
        {
            while (true)
            {
                AgentTurnToken? current = null;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                        yield break;
                    current = enumerator.Current;
                }
                catch (Exception ex)
                {
                    // `yield` isn't allowed inside a catch block, so stash the exception and
                    // handle/yield below.
                    turnFailure = ex;
                }

                if (turnFailure is not null)
                {
                    if (turnFailure is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    {
                        // Expected (client disconnect), not a bug — Warning, not Error. The
                        // finally still persists the trace: #952's log evidence showed
                        // conversations with no assistant message at all, and a disconnect is
                        // one plausible cause.
                        _logger.LogWarning(
                            "Agent turn cancelled (likely client disconnect) before completion for conversation {ConversationId}",
                            conversation.Id);
                    }
                    else
                    {
                        _logger.LogError(turnFailure,
                            "Agent turn failed before completion for conversation {ConversationId}", conversation.Id);
                    }
                    // Bill the failed turn's real usage, not zeros — the same totals
                    // AppendFailureMessage/_rateLimit.Record use below, so this streamed
                    // frame agrees with what got persisted and billed instead of telling
                    // /Agent/Ask consumers the turn cost nothing (nobodies-collective/Humans#990).
                    yield return new AgentTurnToken(null, null,
                        new AgentTurnFinalizer(
                            turnUsage.PromptTokens, turnUsage.OutputTokens,
                            turnUsage.CacheReadTokens, turnUsage.CacheCreationTokens,
                            settings.Model, "error", conversation.Id));
                    yield break;
                }

                // RunTurnAsync yields its finalizer last, after AppendMessageAsync and its own
                // _rateLimit.Record — seeing one means the turn is fully persisted and billed,
                // so a disconnect while writing that very frame must not double up on either.
                if (current!.Finalizer is not null)
                    assistantPersisted = true;
                yield return current;
            }
        }
        finally
        {
            // Reached by `yield break` above and also by disposal: not every abandoned turn
            // throws into the catch, because AgentController awaits WriteSse OUTSIDE this
            // method — a browser that disconnects while a token is being written tears the turn
            // down by disposing the iterator at a `yield return`, which resumes here rather
            // than at MoveNextAsync. Without that the disconnect case #963 set out to fix still
            // leaves a user message with no assistant reply.
            if (!assistantPersisted)
            {
                if (turnFailure is null)
                    _logger.LogWarning(
                        "Agent turn abandoned mid-stream (likely client disconnect) for conversation {ConversationId}",
                        conversation.Id);
                // CancellationToken.None: the turn may be failing BECAUSE cancellationToken
                // fired (client disconnect), and the whole point is to still leave a trace.
                await AppendFailureMessage(conversation.Id, "error", settings.Model, turnUsage, CancellationToken.None);
                // Bill it. The provider already charged us for whatever the turn consumed
                // before it broke, so leaving those tokens out understates admin spend and the
                // DailyTokenCap; and a turn that fails deterministically must still cost a
                // message, or a repeatable backend error becomes an unmetered send loop.
                _rateLimit.Record(request.UserId, today, hour,
                    messagesDelta: 1, tokensDelta: turnUsage.PromptTokens + turnUsage.OutputTokens);
            }
        }
    }

    /// <summary>Provider usage accumulated by <see cref="RunTurnAsync"/> as its tool loop runs.
    /// Mutable and passed in by <see cref="AskAsync"/> rather than kept as iterator locals so a
    /// turn that dies partway can still be billed for the tokens already spent on it.</summary>
    private sealed class TurnUsage
    {
        public int PromptTokens;
        public int OutputTokens;
        public int CacheReadTokens;
        public int CacheCreationTokens;
    }

    /// <summary>The tool-call loop and finalizer for one turn, run after the user message is
    /// already persisted. Split out of <see cref="AskAsync"/> so the caller can wrap iteration
    /// in try/catch (nobodies-collective/Humans#963) — a `yield`-containing method can't have a
    /// `catch` in its own body around the `yield`.</summary>
    private async IAsyncEnumerable<AgentTurnToken> RunTurnAsync(
        AgentTurnRequest request,
        AgentConversation conversation,
        AgentSettingsDto settings,
        List<AgentMessage> priorTurns,
        LocalDate today,
        int hour,
        TurnUsage usage,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
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
        AgentIssueProposal? issueProposal = null;
        AgentTurnFinalizer? finalFinalizer = null;
        // Each loop iteration is a separate provider request with its own usage.
        // Accumulate across all of them — recording only the last finalizer would
        // drop tool-loop (and cap-hit synthesis) requests from admin spend and
        // the DailyTokenCap accounting. The totals live on the caller-supplied
        // `usage` so a turn that throws mid-loop can still be billed for them.
        // Wall-clock turn duration (streaming + tool loop) for the admin status latency panel.
        var turnStart = _clock.GetCurrentInstant();

        // Set when the tool-call cap is hit: the next (final) model call withholds tool
        // use so the model must synthesize an answer from the tool results it already
        // has, instead of the turn dead-ending on interim preamble text.
        var withholdTools = false;

        while (true)
        {
            var iterationAssistantText = new StringBuilder();
            var pendingToolCalls = new List<AnthropicToolCall>();

            await foreach (var token in _client.StreamAsync(
                new AnthropicRequest(settings.Model, systemPrompt, sdkMessages, tools,
                    MaxOutputTokens: MaxOutputTokensPerIteration, DisallowToolUse: withholdTools),
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
                    usage.PromptTokens += f.InputTokens;
                    usage.OutputTokens += f.OutputTokens;
                    usage.CacheReadTokens += f.CacheReadTokens;
                    usage.CacheCreationTokens += f.CacheCreationTokens;
                }
            }

            // A max_tokens cutoff mid tool-call JSON still yields a (possibly truncated)
            // AnthropicToolCall — AnthropicClient closes the current content block before the
            // stream ends regardless of stop reason (nobodies-collective/Humans#963). Treat it
            // like tool_use so the call is dispatched instead of silently discarded: the
            // dispatcher already handles malformed JSON gracefully, and MaxToolCallsPerTurn
            // still bounds the loop if the model keeps truncating.
            var stopReason = finalFinalizer?.StopReason;
            var continuesToolLoop = string.Equals(stopReason, "tool_use", StringComparison.Ordinal)
                || string.Equals(stopReason, "max_tokens", StringComparison.Ordinal);
            if (withholdTools || pendingToolCalls.Count == 0 || !continuesToolLoop)
                break;

            // Replay the calls with unparseable arguments neutralised — see
            // ReplayableToolCalls. Dispatch below still uses the raw pendingToolCalls
            // so the dispatcher sees (and reports) the malformed payload as it is.
            sdkMessages.Add(new AnthropicMessage(
                Role: "assistant",
                Text: iterationAssistantText.Length > 0 ? iterationAssistantText.ToString() : null,
                ToolCalls: ReplayableToolCalls(pendingToolCalls),
                ToolResults: null));

            var results = new List<AnthropicToolResult>();
            foreach (var call in pendingToolCalls)
            {
                toolCallCount++;
                if (toolCallCount > _anthropicOptions.MaxToolCallsPerTurn)
                {
                    // Not `break`: every tool_use block needs a matching tool_result or
                    // the follow-up synthesis call is rejected by the API.
                    results.Add(new AnthropicToolResult(call.Id,
                        "Lookup budget for this turn is used up. Answer now from the information already gathered.",
                        IsError: true));
                    continue;
                }

                var result = await _tools.DispatchAsync(call, request.UserId, cancellationToken);
                results.Add(result);

                if (string.Equals(call.Name, AgentToolNames.RouteToIssue, StringComparison.Ordinal))
                {
                    var proposal = result.IsError ? null : ParseIssueProposalArgs(call.JsonArguments, conversation.Id);
                    if (proposal is not null)
                    {
                        issueProposal = proposal;
                        // The route_to_issue slug in FetchedDocs is the handoff marker
                        // (AgentRepository handoffsOnly / AgentApiController.IsHandoff),
                        // so record it only when the proposal actually reaches the user —
                        // failed dispatches must not inflate handoff counts.
                        fetchedDocs.Add(NormalizeFetchedDocSlug(call.Name, call.JsonArguments, _logger));
                    }
                }
                else if (!result.IsError)
                {
                    // Normalize slug so admin "Top fetched docs" groups by document, not tool-name+args.
                    // Only successful dispatches count: now that max_tokens-truncated calls are
                    // dispatched rather than discarded, a malformed-JSON call would otherwise be
                    // recorded under its bare tool name (NormalizeFetchedDocSlug's parse fallback)
                    // and inflate the admin panel with lookups that never returned a document.
                    fetchedDocs.Add(NormalizeFetchedDocSlug(call.Name, call.JsonArguments, _logger));
                }
            }

            sdkMessages.Add(new AnthropicMessage("tool", Text: null, ToolCalls: null, ToolResults: results));

            // route_to_issue handoff: the proposal frame is the terminal output; no synthesis call.
            if (issueProposal is not null)
                break;

            // Cap reached: loop once more with tool use withheld so the model answers
            // from the results above instead of stranding the user mid-lookup.
            if (toolCallCount >= _anthropicOptions.MaxToolCallsPerTurn)
                withholdTools = true;
        }

        // Never end a turn silently (nobodies-collective/Humans#952): a truncated or
        // exhausted tool loop can leave assistantBuffer empty, which used to persist
        // and yield a blank bubble. Fill in fallback prose so the transcript and the
        // admin conversation view always show what the user saw.
        var assistantText = assistantBuffer.ToString();
        if (string.IsNullOrWhiteSpace(assistantText))
        {
            if (issueProposal is not null)
            {
                // The proposal frame is the terminal output for route_to_issue, but a
                // blank stored message makes the admin conversation view misleading.
                // Persist the fallback without streaming it: when no prose arrives the
                // widget renders its own localized handoff line live, and a streamed
                // delta would override that localization. The persisted copy mirrors
                // the widget's Help_Agent_IssueProposed strings so a history reload
                // shows the same sentence the user saw.
                assistantText = RouteToIssueFallbackText(conversation.Locale);
            }
            else
            {
                _logger.LogWarning(
                    "Agent turn produced no assistant text for conversation {ConversationId}: {ToolCallCount} tool calls, stop reason {StopReason}",
                    conversation.Id, toolCallCount, finalFinalizer?.StopReason ?? "unknown");
                assistantText = SilentTurnFallbackText(conversation.Locale);
                yield return new AgentTurnToken(assistantText, null, null);
            }
        }

        // Proposal frame signals client to open pre-filled Issues modal.
        if (issueProposal is not null)
        {
            yield return new AgentTurnToken(null, null, null, issueProposal);
        }

        var turnEnd = _clock.GetCurrentInstant();
        var durationMs = (int)Math.Min(
            int.MaxValue,
            (turnEnd - turnStart).TotalMilliseconds);
        var message = new AgentMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Role = AgentRole.Assistant,
            Content = assistantText,
            CreatedAt = turnEnd,
            PromptTokens = usage.PromptTokens,
            OutputTokens = usage.OutputTokens,
            CachedTokens = usage.CacheReadTokens,
            Model = settings.Model,
            DurationMs = durationMs,
            FetchedDocs = fetchedDocs.ToArray(),
            HandedOffToFeedbackId = null
        };
        await _repo.AppendMessageAsync(message, cancellationToken);

        var totalTokens = message.PromptTokens + message.OutputTokens;
        _rateLimit.Record(request.UserId, today, hour, messagesDelta: 1, tokensDelta: totalTokens);

        var fallbackFinalizer = finalFinalizer ?? new AgentTurnFinalizer(0, 0, 0, 0, _settings.Current.Model, "unknown");
        // Stamp the conversation id so the client can reuse it on the next send, and
        // the turn-wide token sums so the finalizer reflects the whole turn, not just
        // the last provider request. Other fields (stop reason, model) stay from the last one.
        yield return new AgentTurnToken(null, null, fallbackFinalizer with
        {
            ConversationId = conversation.Id,
            InputTokens = usage.PromptTokens,
            OutputTokens = usage.OutputTokens,
            CacheReadTokens = usage.CacheReadTokens,
            CacheCreationTokens = usage.CacheCreationTokens
        });
    }

    /// <summary>How many prior user/assistant turns to replay (bounded for context budget).</summary>
    private const int HistoryReplayLimit = 20;

    /// <summary>Per-iteration output cap sent to the provider. 1024 (the prior value) was tight
    /// for a turn that also emits tool-call JSON, truncating mid-JSON often enough to matter
    /// (nobodies-collective/Humans#963). Raised alongside the max_tokens loop-continuation fix
    /// above — the two are complementary, not alternatives: the higher cap avoids most
    /// truncation outright, and the loop fix handles what it doesn't.</summary>
    private const int MaxOutputTokensPerIteration = 4096;

    /// <summary>Shown when a turn ends with no assistant prose (exhausted/truncated tool loop).
    /// Localized here because the Application layer has no resx access and the text is both
    /// streamed and persisted; locale codes mirror <c>User.PreferredLanguage</c>.</summary>
    private static string SilentTurnFallbackText(string? locale) => locale switch
    {
        "es" => "No he podido preparar una respuesta para eso. ¿Podrías reformular la pregunta o intentarlo de nuevo?",
        "ca" => "No he pogut preparar una resposta per a això. Podries reformular la pregunta o tornar-ho a intentar?",
        "de" => "Ich konnte dazu keine Antwort zusammenstellen — kannst du die Frage umformulieren oder es noch einmal versuchen?",
        "fr" => "Je n'ai pas réussi à formuler une réponse — peux-tu reformuler ta question ou réessayer ?",
        "it" => "Non sono riuscito a mettere insieme una risposta — puoi riformulare la domanda o riprovare?",
        _ => "I wasn't able to put together an answer for that — could you try rephrasing or asking again?",
    };

    /// <summary>Persisted when a route_to_issue handoff produced no preamble text of its own.
    /// Kept in lockstep with the widget's <c>Help_Agent_IssueProposed</c> resx strings, which
    /// render the live bubble for this case.</summary>
    private static string RouteToIssueFallbackText(string? locale) => locale switch
    {
        "es" => "No puedo responder a esto por mí mismo. He redactado una incidencia para el equipo — revísala y envíala, por favor.",
        "ca" => "No puc respondre això jo mateix. He redactat una incidència per a l'equip — revisa-la i envia-la, si us plau.",
        "de" => "Das kann ich selbst nicht beantworten. Ich habe einen Vorgang für das Team entworfen — bitte prüfe und sende ihn ab.",
        "fr" => "Je ne peux pas répondre à cela moi-même. J'ai rédigé un signalement pour l'équipe — relis-le et envoie-le, s'il te plaît.",
        "it" => "Non posso rispondere a questo da solo. Ho preparato una segnalazione per il team — controllala e inviala, per favore.",
        _ => "I can't answer this myself. I've drafted an issue for the team — please review and submit it.",
    };

    public async Task<IReadOnlyList<AgentConversationListSnapshot>> GetHistoryAsync(
        Guid userId, int take, CancellationToken ct)
    {
        var conversations = await _repo.ListConversationsForUserAsync(userId, take, ct);
        return conversations.Select(ToListSnapshot).ToList();
    }

    public async Task<AgentConversationTranscriptSnapshot?> GetConversationForUserAsync(
        Guid userId, Guid conversationId, CancellationToken ct)
    {
        var conv = await _repo.GetConversationByIdAsync(conversationId, ct);
        return conv is not null && conv.UserId == userId ? ToTranscriptSnapshot(conv) : null;
    }

    public async Task<AgentMyConversationView?> GetMyConversationAsync(
        Guid userId, Guid conversationId, CancellationToken ct)
    {
        var conv = await _repo.GetConversationByIdAsync(conversationId, ct);
        // Ownership mismatch returns null like "not found" (Agent.md invariant 7) — no existence leak.
        if (conv is null || conv.UserId != userId) return null;

        // Tail regenerated from current snapshot; may differ from historical turn view (Agent.md open question).
        var snapshot = await _snapshots.LoadAsync(userId, ct);
        var tail = _assembler.BuildUserContextTail(snapshot);
        return new AgentMyConversationView(ToTranscriptSnapshot(conv), tail);
    }

    public async Task<IReadOnlyList<AgentConversationListSnapshot>> ListAllConversationsForAdminAsync(
        bool refusalsOnly, Guid? userId, int take, int skip,
        CancellationToken ct)
    {
        var conversations = await _repo.ListAllConversationsAsync(refusalsOnly, userId, take, skip, ct);
        return conversations.Select(ToListSnapshot).ToList();
    }

    public async Task<IReadOnlyList<AgentConversationTranscriptSnapshot>> ListAllConversationsForAdminWithMessagesAsync(
        bool refusalsOnly, bool handoffsOnly, Guid? userId, int take, int skip,
        CancellationToken ct)
    {
        var conversations = await _repo.ListAllConversationsWithMessagesAsync(
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
        var conversation = await _repo.GetConversationByIdAsync(id, ct);
        return conversation is null ? null : ToTranscriptSnapshot(conversation);
    }

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

        // Measure the system prompt with the real tokenizer (count_tokens). This is a diagnostic
        // nicety — a failed/rate-limited count must never break the admin page, so null on error.
        int? systemPromptTokens = null;
        try
        {
            systemPromptTokens = await _client.CountTokensAsync(settings.Model, systemPrompt, ct);
        }
        catch (Exception ex)
        {
            // Expected/transient (rate limit, network) — log the reason at Warning, drop the
            // stack trace per memory/code/always-log-problems.md.
            _logger.LogWarning(
                "count_tokens failed for prompt preview; rendering without a token count: {Reason}", ex.Message);
        }

        return new AgentPromptPreview(
            Model: settings.Model,
            SystemPrompt: systemPrompt,
            UserContextTail: tail,
            Tools: tools,
            ReplayedHistory: replayed,
            SystemPromptTokens: systemPromptTokens);
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

    /// <summary>
    /// Build a stable, low-cardinality slug for the <c>FetchedDocs</c> column
    /// so the admin status "Top fetched docs" panel groups identical fetches
    /// together. For doc-style tools (<c>fetch_section_guide</c>,
    /// <c>fetch_feature_spec</c>) the slug is <c>tool:argument</c>. For
    /// non-doc tools we drop the JSON args entirely — different shift ids /
    /// audit limits would otherwise split the bucket per invocation.
    /// </summary>
    /// <summary>
    /// Returns the tool calls as they can safely be replayed to the provider.
    /// </summary>
    /// <remarks>
    /// A max_tokens cutoff mid tool-call JSON yields a truncated arguments payload
    /// (nobodies-collective/Humans#963). Every following request replays the whole
    /// assistant message, and <c>AnthropicClient.MapMessages</c> deserializes each
    /// replayed <c>tool_use</c> block's arguments into a
    /// <c>Dictionary&lt;string, JsonElement&gt;</c> — so replaying the raw truncated
    /// payload throws while building the request, killing the very recovery
    /// iteration this is supposed to enable.
    /// <para>
    /// Unparseable arguments are therefore swapped for an empty object. The block
    /// still pairs with its <c>tool_result</c> (the API rejects an unmatched
    /// <c>tool_use</c>), and that result carries <c>IsError</c>, so the model is
    /// told the call failed and can reissue it rather than silently seeing a
    /// well-formed no-arg call.
    /// </para>
    /// </remarks>
    private static List<AnthropicToolCall> ReplayableToolCalls(List<AnthropicToolCall> calls) =>
        [.. calls.Select(c => IsReplayableArguments(c.JsonArguments) ? c : c with { JsonArguments = "{}" })];

    private static bool IsReplayableArguments(string jsonArguments)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(jsonArguments);
            // MapMessages deserializes into a dictionary, so a valid non-object
            // (array, bare string) would throw there just like malformed JSON does.
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static string NormalizeFetchedDocSlug(string toolName, string jsonArguments, ILogger<AgentService> logger)
    {
        switch (toolName)
        {
            case AgentToolNames.FetchSectionGuide:
            case AgentToolNames.FetchFeatureSpec:
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(jsonArguments);
                    var root = doc.RootElement;
                    string? slug = null;
                    if (string.Equals(toolName, AgentToolNames.FetchSectionGuide, StringComparison.Ordinal)
                        && root.TryGetProperty("section", out var s))
                        slug = s.GetString();
                    else if (string.Equals(toolName, AgentToolNames.FetchFeatureSpec, StringComparison.Ordinal)
                        && root.TryGetProperty("name", out var n))
                        slug = n.GetString();
                    return string.IsNullOrEmpty(slug) ? toolName : $"{toolName}:{slug}";
                }
                catch (System.Text.Json.JsonException ex)
                {
                    logger.LogWarning(ex, "Failed to parse JSON args for tool {ToolName}; FetchedDocs slug falls back to bare tool name", toolName);
                    return toolName;
                }
            default:
                return toolName;
        }
    }

    private AgentIssueProposal? ParseIssueProposalArgs(string jsonArguments, Guid conversationId)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(jsonArguments);
            var root = doc.RootElement;
            var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var description = root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            var categoryRaw = root.TryGetProperty("category", out var c) ? c.GetString() : null;
            var category = Enum.TryParse<IssueCategory>(categoryRaw, ignoreCase: true, out var parsed)
                ? parsed
                : IssueCategory.Question;

            // Trim to the same caps the issues form enforces; the agent's
            // suggestion sometimes runs over.
            if (title.Length > 200) title = title[..200];
            if (description.Length > 5000) description = description[..5000];

            return new AgentIssueProposal(title, category, description);
        }
        catch (System.Text.Json.JsonException)
        {
            _logger.LogWarning(
                "route_to_issue args could not be parsed for conversation {ConversationId}; proposal dropped. Args: {Args}",
                conversationId, jsonArguments);
            return null;
        }
    }

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

        // usage: null — a refused turn is rejected before any provider call, so it cost nothing.
        await AppendFailureMessage(conv.Id, reason, _settings.Current.Model, usage: null, ct);
    }

    /// <summary>Appends an empty-content assistant message carrying a machine-readable
    /// <c>RefusalReason</c> — the same "no real answer, here's why" shape <see cref="PersistRefusal"/>
    /// already writes for rate-limit/abuse turns (Agent.md invariant 6). Reused for the
    /// turn-exception path (nobodies-collective/Humans#963) so a failed turn shows up through the
    /// same admin refusals filter and "top refusal reasons" panel instead of a new surface.</summary>
    /// <param name="usage">Provider usage to stamp on the row, or null for a turn that never
    /// reached the provider (rate_limited / abuse_flag). A turn that failed mid-flight did spend
    /// tokens, and <see cref="AgentAdminStatusService"/> prices spend straight off
    /// <see cref="AgentMessage.PromptTokens"/>/<see cref="AgentMessage.OutputTokens"/> — leaving
    /// zeros here would hide billable turns from the admin panel even though the rate-limit
    /// store counted them.</param>
    private async Task AppendFailureMessage(
        Guid conversationId, string reason, string model, TurnUsage? usage, CancellationToken ct)
    {
        await _repo.AppendMessageAsync(new AgentMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Role = AgentRole.Assistant,
            Content = "",
            CreatedAt = _clock.GetCurrentInstant(),
            PromptTokens = usage?.PromptTokens ?? 0,
            OutputTokens = usage?.OutputTokens ?? 0,
            CachedTokens = usage?.CacheReadTokens ?? 0,
            Model = model,
            RefusalReason = reason
        }, ct);
    }
}

using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Anthropic.Models.Models;
using Humans.Base.Extensions;
using Microsoft.Extensions.Options;
using SdkAnthropicClient = Anthropic.AnthropicClient;
using Humans.Agent.Models;

namespace Humans.Agent.Services.Anthropic;

internal sealed class AnthropicClient : IAnthropicClient
{
    private readonly SdkAnthropicClient _sdk;
    private readonly ILogger<AnthropicClient> _logger;

    public AnthropicClient(IOptions<AnthropicOptions> options, ILogger<AnthropicClient> logger)
    {
        var opts = options.Value;
        _sdk = new SdkAnthropicClient(new ClientOptions
        {
            ApiKey = opts.ApiKey,
            Timeout = opts.Timeout,
        });
        _logger = logger;
    }

    internal AnthropicClient(SdkAnthropicClient sdk, ILogger<AnthropicClient> logger)
    {
        _sdk = sdk;
        _logger = logger;
    }

    public async IAsyncEnumerable<AgentTurnToken> StreamAsync(
        AnthropicRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var timer = _logger.TimeOperation();
        var sdkRequest = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxOutputTokens,
            System = new List<TextBlockParam>
            {
                new(request.SystemCacheablePrefix)
                {
                    CacheControl = new CacheControlEphemeral(),
                },
            },
            Messages = MapMessages(request.Messages),
            Tools = request.Tools
                .Select(t => (ToolUnion)new Tool
                {
                    Name = t.Name,
                    Description = t.Description,
                    InputSchema = MapInputSchema(t.JsonSchema),
                })
                .ToList(),
            ToolChoice = request.DisallowToolUse ? new ToolChoice(new ToolChoiceNone()) : null,
        };

        string? model = null;
        long inputTokens = 0;
        long outputTokens = 0;
        long cacheReadTokens = 0;
        long cacheCreationTokens = 0;
        string? stopReason = null;
        bool streamErrored = false;
        bool streamCompleted = false;

        // Tool args stream as input_json_delta; accumulate per-block and emit on content_block_stop.
        var pendingToolBlocks = new Dictionary<long, (string Id, string Name, System.Text.StringBuilder Json)>();
        // Every block of the response by index, so a tool-use turn can be replayed exactly as produced.
        var responseBlocks = new SortedDictionary<long, ResponseBlock>();

        AgentTurnFinalizer Snapshot(string? reason = null) => new(
            ClampToInt(inputTokens), ClampToInt(outputTokens), ClampToInt(cacheReadTokens),
            ClampToInt(cacheCreationTokens), model ?? request.Model, reason);

        // Stream failures get a synthetic "error" finalizer so AgentService can complete the SSE frame.
        var enumerator = _sdk.Messages.CreateStreaming(sdkRequest, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Anthropic streaming call failed; emitting synthetic error finalizer.");
                    streamErrored = true;
                    break;
                }

                if (!moved) break;
                var evt = enumerator.Current;

                if (evt.TryPickStart(out var startEvent))
                {
                    model = startEvent.Message.Model;
                    inputTokens = startEvent.Message.Usage.InputTokens;
                    outputTokens = startEvent.Message.Usage.OutputTokens;
                    cacheReadTokens = startEvent.Message.Usage.CacheReadInputTokens ?? 0;
                    cacheCreationTokens = startEvent.Message.Usage.CacheCreationInputTokens ?? 0;
                    yield return new AgentTurnToken(null, null, null, Usage: Snapshot());
                    continue;
                }

                if (evt.TryPickContentBlockDelta(out var deltaEvent))
                {
                    if (deltaEvent.Delta.TryPickText(out var textDelta))
                    {
                        if (responseBlocks.TryGetValue(deltaEvent.Index, out var textBlock))
                            textBlock.Content.Append(textDelta.Text);
                        yield return new AgentTurnToken(textDelta.Text, null, null);
                    }
                    else if (deltaEvent.Delta.TryPickThinking(out var thinkingDelta))
                    {
                        if (responseBlocks.TryGetValue(deltaEvent.Index, out var thinkingBlock))
                            thinkingBlock.Content.Append(thinkingDelta.Thinking);
                    }
                    else if (deltaEvent.Delta.TryPickSignature(out var signatureDelta))
                    {
                        if (responseBlocks.TryGetValue(deltaEvent.Index, out var signedBlock))
                            signedBlock.Signature += signatureDelta.Signature;
                    }
                    else if (deltaEvent.Delta.TryPickInputJson(out var inputJsonDelta))
                    {
                        if (pendingToolBlocks.TryGetValue(deltaEvent.Index, out var pending))
                        {
                            pending.Json.Append(inputJsonDelta.PartialJson);
                        }
                    }
                    continue;
                }

                if (evt.TryPickContentBlockStart(out var blockStartEvent))
                {
                    var started = blockStartEvent.ContentBlock;
                    if (started.TryPickText(out var startedText))
                        responseBlocks[blockStartEvent.Index] = new ResponseBlock(ResponseBlockKind.Text, startedText.Text);
                    else if (started.TryPickThinking(out var startedThinking))
                        responseBlocks[blockStartEvent.Index] = new ResponseBlock(ResponseBlockKind.Thinking, startedThinking.Thinking)
                        {
                            Signature = startedThinking.Signature,
                        };
                    else if (started.TryPickRedactedThinking(out var redacted))
                        responseBlocks[blockStartEvent.Index] = new ResponseBlock(ResponseBlockKind.RedactedThinking, redacted.Data);

                    if (started.TryPickToolUse(out var toolUseBlock))
                    {
                        pendingToolBlocks[blockStartEvent.Index] = (
                            toolUseBlock.ID,
                            toolUseBlock.Name,
                            new System.Text.StringBuilder());
                    }
                    continue;
                }

                if (evt.TryPickContentBlockStop(out var blockStopEvent))
                {
                    if (pendingToolBlocks.Remove(blockStopEvent.Index, out var completed))
                    {
                        // "{}" so JsonDocument.Parse won't fail on zero-arg tools.
                        var jsonArgs = completed.Json.Length > 0 ? completed.Json.ToString() : "{}";
                        var call = new AnthropicToolCall(completed.Id, completed.Name, jsonArgs);
                        responseBlocks[blockStopEvent.Index] = new ResponseBlock(ResponseBlockKind.ToolUse, "") { ToolCall = call };
                        yield return new AgentTurnToken(null, call, null);
                    }
                    continue;
                }

                if (evt.TryPickDelta(out var messageDeltaEvent))
                {
                    // Usage here is cumulative.
                    outputTokens = messageDeltaEvent.Usage.OutputTokens;
                    cacheReadTokens = messageDeltaEvent.Usage.CacheReadInputTokens ?? cacheReadTokens;
                    cacheCreationTokens = messageDeltaEvent.Usage.CacheCreationInputTokens ?? cacheCreationTokens;

                    var sr = messageDeltaEvent.Delta.StopReason;
                    if (sr is not null)
                    {
                        stopReason = sr.Raw();
                    }
                    yield return new AgentTurnToken(null, null, null, Usage: Snapshot());
                    continue;
                }

                if (evt.TryPickStop(out _))
                {
                    streamCompleted = true;
                    yield return new AgentTurnToken(
                        null,
                        null,
                        Snapshot(stopReason),
                        AssistantContent: [.. responseBlocks.Values.Select(b => b.ToContentBlock())]);
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        if (!streamCompleted)
        {
            if (!streamErrored)
                _logger.LogError("Anthropic stream ended before message_stop; preserving known usage in an error finalizer.");
            yield return new AgentTurnToken(
                null,
                null,
                Snapshot("error"));
        }
    }

    public async Task<int> CountTokensAsync(
        string model, string text, CancellationToken cancellationToken = default)
    {
        // Counted as the cached System block, the way it is sent. count_tokens needs a non-empty messages array, so a
        // placeholder rides along: hence ≈, not exact.
        var request = new MessageCountTokensParams
        {
            Model = model,
            System = new List<TextBlockParam> { new(text) },
            Messages = new List<MessageParam>
            {
                new UserMessageParam(new List<ContentBlockParam> { new(new TextBlockParam(".")) }),
            },
        };

        var result = await _sdk.Messages.CountTokens(request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return ClampToInt(result.InputTokens);
    }

    public async Task<IReadOnlyList<AnthropicModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var page = await _sdk.Models.List(new ModelListParams { Limit = 100 }, cancellationToken).ConfigureAwait(false);
        return page.Items.Select(m => new AnthropicModelInfo(m.ID, m.DisplayName)).ToList();
    }

    private static int ClampToInt(long value) => value > int.MaxValue ? int.MaxValue : (int)value;

    private static List<MessageParam> MapMessages(IReadOnlyList<AnthropicMessage> messages)
    {
        var result = new List<MessageParam>(messages.Count);

        foreach (var msg in messages)
        {
            var contentBlocks = new List<ContentBlockParam>();

            if (msg.AssistantContent is not null)
            {
                contentBlocks.AddRange(msg.AssistantContent.Select(MapAssistantBlock));
            }
            else if (msg.Text is not null)
            {
                contentBlocks.Add(new ContentBlockParam(new TextBlockParam(msg.Text)));
            }

            if (msg.AssistantContent is null && msg.ToolCalls is not null)
            {
                foreach (var tc in msg.ToolCalls)
                {
                    contentBlocks.Add(MapToolUse(tc));
                }
            }

            if (msg.ToolResults is not null)
            {
                foreach (var tr in msg.ToolResults)
                {
                    contentBlocks.Add(new ContentBlockParam(
                        new ToolResultBlockParam(tr.ToolCallId)
                        {
                            Content = (ToolResultBlockParamContent)tr.Content,
                            IsError = tr.IsError,
                        }));
                }
            }

            result.Add(string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? new AssistantMessageParam(contentBlocks)
                : new UserMessageParam(contentBlocks));
        }

        return result;
    }

    private static ContentBlockParam MapAssistantBlock(AnthropicContentBlock block) => block switch
    {
        AnthropicTextBlock t => new ContentBlockParam(new TextBlockParam(t.Text)),
        AnthropicThinkingBlock t => new ContentBlockParam(new ThinkingBlockParam { Thinking = t.Thinking, Signature = t.Signature }),
        AnthropicRedactedThinkingBlock r => new ContentBlockParam(new RedactedThinkingBlockParam { Data = r.Data }),
        AnthropicToolUseBlock u => MapToolUse(u.Call),
        _ => throw new ArgumentOutOfRangeException(nameof(block), block.GetType().Name, "Unknown assistant block"),
    };

    private static ContentBlockParam MapToolUse(AnthropicToolCall call)
    {
        var input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(call.JsonArguments)
                    ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        return new ContentBlockParam(new ToolUseBlockParam { ID = call.Id, Name = call.Name, Input = input });
    }

    private enum ResponseBlockKind { Text, Thinking, RedactedThinking, ToolUse }

    private sealed class ResponseBlock(ResponseBlockKind kind, string initial)
    {
        public System.Text.StringBuilder Content { get; } = new(initial);
        public string Signature { get; set; } = "";
        public AnthropicToolCall? ToolCall { get; init; }

        public AnthropicContentBlock ToContentBlock() => kind switch
        {
            ResponseBlockKind.Text => new AnthropicTextBlock(Content.ToString()),
            ResponseBlockKind.Thinking => new AnthropicThinkingBlock(Content.ToString(), Signature),
            ResponseBlockKind.RedactedThinking => new AnthropicRedactedThinkingBlock(Content.ToString()),
            _ => new AnthropicToolUseBlock(ToolCall!),
        };
    }

    private static InputSchema MapInputSchema(string jsonSchema)
    {
        var rawData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonSchema)
                     ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        return InputSchema.FromRawUnchecked(rawData);
    }
}

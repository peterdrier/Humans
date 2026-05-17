using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Application.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SdkAnthropicClient = Anthropic.AnthropicClient;

namespace Humans.Infrastructure.Services.Anthropic;

/// <summary>Thin wrapper around the Anthropic .NET SDK. Adapts <see cref="AnthropicRequest"/>
/// to the SDK surface and re-emits streaming events as <see cref="AgentTurnToken"/> records.</summary>
public sealed class AnthropicClient : IAnthropicClient
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

    public async IAsyncEnumerable<AgentTurnToken> StreamAsync(
        AnthropicRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
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
        };

        string? model = null;
        long inputTokens = 0;
        long outputTokens = 0;
        long cacheReadTokens = 0;
        long cacheCreationTokens = 0;
        string? stopReason = null;
        bool streamErrored = false;

        // Tool args stream as input_json_delta; accumulate per-block and emit on content_block_stop.
        var pendingToolBlocks = new Dictionary<long, (string Id, string Name, System.Text.StringBuilder Json)>();

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
                    continue;
                }

                if (evt.TryPickContentBlockDelta(out var deltaEvent))
                {
                    if (deltaEvent.Delta.TryPickText(out var textDelta))
                    {
                        yield return new AgentTurnToken(textDelta.Text, null, null);
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
                    if (blockStartEvent.ContentBlock.TryPickToolUse(out var toolUseBlock))
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
                        yield return new AgentTurnToken(
                            null,
                            new AnthropicToolCall(completed.Id, completed.Name, jsonArgs),
                            null);
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
                    continue;
                }

                if (evt.TryPickStop(out _))
                {
                    yield return new AgentTurnToken(
                        null,
                        null,
                        new AgentTurnFinalizer(
                            ClampToInt(inputTokens),
                            ClampToInt(outputTokens),
                            ClampToInt(cacheReadTokens),
                            ClampToInt(cacheCreationTokens),
                            model ?? request.Model,
                            stopReason));
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        if (streamErrored)
        {
            yield return new AgentTurnToken(
                null,
                null,
                new AgentTurnFinalizer(
                    ClampToInt(inputTokens),
                    ClampToInt(outputTokens),
                    ClampToInt(cacheReadTokens),
                    ClampToInt(cacheCreationTokens),
                    model ?? request.Model,
                    "error"));
        }
    }

    private static int ClampToInt(long value) => value > int.MaxValue ? int.MaxValue : (int)value;

    private static List<MessageParam> MapMessages(IReadOnlyList<AnthropicMessage> messages)
    {
        var result = new List<MessageParam>(messages.Count);

        foreach (var msg in messages)
        {
            var role = string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? Role.Assistant
                : Role.User;

            var contentBlocks = new List<ContentBlockParam>();

            if (msg.Text is not null)
            {
                contentBlocks.Add(new ContentBlockParam(new TextBlockParam(msg.Text), default));
            }

            if (msg.ToolCalls is not null)
            {
                foreach (var tc in msg.ToolCalls)
                {
                    var input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(tc.JsonArguments)
                                ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    contentBlocks.Add(new ContentBlockParam(
                        new ToolUseBlockParam { ID = tc.Id, Name = tc.Name, Input = input },
                        default));
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
                        },
                        default));
                }
            }

            result.Add(new MessageParam
            {
                Role = role,
                Content = new MessageParamContent(contentBlocks, default),
            });
        }

        return result;
    }

    private static InputSchema MapInputSchema(string jsonSchema)
    {
        var rawData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonSchema)
                     ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        return InputSchema.FromRawUnchecked(rawData);
    }
}

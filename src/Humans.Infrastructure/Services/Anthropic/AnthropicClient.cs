using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Humans.Application.Interfaces;
using Humans.Application.Models;
using Humans.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using SdkAnthropicClient = Anthropic.AnthropicClient;

namespace Humans.Infrastructure.Services.Anthropic;

/// <summary>Thin wrapper around the Anthropic .NET SDK. Adapts <see cref="AnthropicRequest"/>
/// to the SDK surface and re-emits streaming events as <see cref="AgentTurnToken"/> records.</summary>
public sealed class AnthropicClient : IAnthropicClient
{
    private readonly SdkAnthropicClient _sdk;

    public AnthropicClient(IOptions<AnthropicOptions> options)
    {
        var opts = options.Value;
        _sdk = new SdkAnthropicClient(new ClientOptions
        {
            ApiKey = opts.ApiKey,
            Timeout = opts.Timeout,
        });
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
                new TextBlockParam(request.SystemCacheablePrefix)
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

        // Aggregated state from earlier events for the finalizer.
        string? model = null;
        long inputTokens = 0;
        long outputTokens = 0;
        long cacheReadTokens = 0;
        long cacheCreationTokens = 0;
        string? stopReason = null;

        await foreach (var evt in _sdk.Messages.CreateStreaming(sdkRequest, cancellationToken))
        {
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
                continue;
            }

            if (evt.TryPickContentBlockStart(out var blockStartEvent))
            {
                if (blockStartEvent.ContentBlock.TryPickToolUse(out var toolUseBlock))
                {
                    var jsonArgs = JsonSerializer.Serialize(toolUseBlock.Input);
                    yield return new AgentTurnToken(
                        null,
                        new AnthropicToolCall(toolUseBlock.ID, toolUseBlock.Name, jsonArgs),
                        null);
                }
                continue;
            }

            if (evt.TryPickDelta(out var messageDeltaEvent))
            {
                // Usage in the delta event is cumulative output usage.
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
                // Emit exactly one finalizer at the end of the stream.
                yield return new AgentTurnToken(
                    null,
                    null,
                    new AgentTurnFinalizer(
                        (int)inputTokens,
                        (int)outputTokens,
                        (int)cacheReadTokens,
                        (int)cacheCreationTokens,
                        model ?? request.Model,
                        stopReason));
            }
        }
    }

    private static List<MessageParam> MapMessages(IReadOnlyList<AnthropicMessage> messages)
    {
        var result = new List<MessageParam>(messages.Count);

        foreach (var msg in messages)
        {
            var role = string.Equals(msg.Role, "assistant", System.StringComparison.OrdinalIgnoreCase)
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
        // The SDK's InputSchema holds RawData. We can pass the JSON schema properties
        // through by parsing the JSON and forwarding the raw dictionary.
        var rawData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonSchema)
                     ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        return InputSchema.FromRawUnchecked(rawData);
    }
}

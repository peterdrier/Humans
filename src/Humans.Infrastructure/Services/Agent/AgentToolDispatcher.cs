using System.Globalization;
using System.Text.Json;
using Humans.Application.Constants;
using Humans.Application.Interfaces;
using Humans.Application.Models;
using Humans.Infrastructure.Services.Preload;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services.Agent;

public sealed class AgentToolDispatcher : IAgentToolDispatcher
{
    private readonly AgentSectionDocReader _sections;
    private readonly AgentFeatureSpecReader _features;
    private readonly IFeedbackService _feedback;
    private readonly ILogger<AgentToolDispatcher> _logger;

    public AgentToolDispatcher(
        AgentSectionDocReader sections,
        AgentFeatureSpecReader features,
        IFeedbackService feedback,
        ILogger<AgentToolDispatcher> logger)
    {
        _sections = sections;
        _features = features;
        _feedback = feedback;
        _logger = logger;
    }

    public async Task<AnthropicToolResult> DispatchAsync(
        AnthropicToolCall call, Guid userId, Guid conversationId, CancellationToken cancellationToken)
    {
        if (!AgentToolNames.All.Contains(call.Name))
        {
            _logger.LogWarning("Agent requested unknown tool {ToolName}", call.Name);
            return new AnthropicToolResult(call.Id, string.Create(CultureInfo.InvariantCulture, $"Unknown tool: {call.Name}"), IsError: true);
        }

        try
        {
            using var doc = JsonDocument.Parse(call.JsonArguments);
            var args = doc.RootElement;

            switch (call.Name)
            {
                case AgentToolNames.FetchFeatureSpec:
                {
                    var name = args.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var body = await _features.ReadAsync(name, cancellationToken);
                    return body is null
                        ? new AnthropicToolResult(call.Id, string.Create(CultureInfo.InvariantCulture, $"Feature spec not found: {name}"), IsError: true)
                        : new AnthropicToolResult(call.Id, body, IsError: false);
                }
                case AgentToolNames.FetchSectionGuide:
                {
                    var key = args.TryGetProperty("section", out var s) ? s.GetString() ?? "" : "";
                    var body = await _sections.ReadAsync(key, cancellationToken);
                    return body is null
                        ? new AnthropicToolResult(call.Id, string.Create(CultureInfo.InvariantCulture, $"Unknown section: {key}"), IsError: true)
                        : new AnthropicToolResult(call.Id, body, IsError: false);
                }
                case AgentToolNames.RouteToFeedback:
                {
                    var summary = args.TryGetProperty("summary", out var sm) ? sm.GetString() ?? "" : "";
                    var topic = args.TryGetProperty("topic", out var t) ? t.GetString() ?? "" : "";
                    var handoff = await _feedback.SubmitFromAgentAsync(userId, conversationId, summary, topic, cancellationToken);
                    return new AnthropicToolResult(call.Id,
                        string.Create(CultureInfo.InvariantCulture, $"Handed off. Feedback URL: {handoff.FeedbackUrl}"),
                        IsError: false);
                }
                default:
                    return new AnthropicToolResult(call.Id, string.Create(CultureInfo.InvariantCulture, $"Tool dispatch not implemented: {call.Name}"), IsError: true);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Agent sent malformed JSON arguments for tool {ToolName}", call.Name);
            return new AnthropicToolResult(call.Id, "Malformed tool arguments (expected JSON object).", IsError: true);
        }
    }
}

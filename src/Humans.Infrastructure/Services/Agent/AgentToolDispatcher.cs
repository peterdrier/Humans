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
    private readonly ILogger<AgentToolDispatcher> _logger;

    public AgentToolDispatcher(
        AgentSectionDocReader sections,
        AgentFeatureSpecReader features,
        ILogger<AgentToolDispatcher> logger)
    {
        _sections = sections;
        _features = features;
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
                case AgentToolNames.RouteToIssue:
                    {
                        // No DB write — AgentService inspects the call args and emits an
                        // AgentIssueProposal frame so the client can pre-fill the issue
                        // submission form. The tool result here is just an LLM-facing
                        // confirmation telling it the turn is over.
                        return new AnthropicToolResult(call.Id,
                            "Proposal queued. The system will pre-fill an issue submission form for the user. Stop and await the next user turn.",
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

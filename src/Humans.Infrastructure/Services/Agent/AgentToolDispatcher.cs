using System.Globalization;
using System.Text.Json;
using Humans.Application.Constants;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Models;
using Humans.Infrastructure.Services.Preload;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services.Agent;

public sealed class AgentToolDispatcher : IAgentToolDispatcher
{
    /// <summary>
    /// Default number of audit-history lines surfaced when the agent calls
    /// <see cref="AgentToolNames.GetAuditHistory"/> without a <c>limit</c>.
    /// </summary>
    internal const int DefaultAuditHistoryLimit = 20;

    /// <summary>
    /// Hard cap on audit-history lines per call. Prevents the agent from
    /// pulling unbounded history in one tool turn.
    /// </summary>
    internal const int MaxAuditHistoryLimit = 50;

    private readonly AgentSectionDocReader _sections;
    private readonly AgentFeatureSpecReader _features;
    private readonly IAuditViewerService _auditViewer;
    private readonly ILogger<AgentToolDispatcher> _logger;

    public AgentToolDispatcher(
        AgentSectionDocReader sections,
        AgentFeatureSpecReader features,
        IAuditViewerService auditViewer,
        ILogger<AgentToolDispatcher> logger)
    {
        _sections = sections;
        _features = features;
        _auditViewer = auditViewer;
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
                case AgentToolNames.GetAuditHistory:
                    {
                        var limit = ParseAuditHistoryLimit(args);
                        return await DispatchGetAuditHistoryAsync(call.Id, userId, limit, cancellationToken);
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

    private async Task<AnthropicToolResult> DispatchGetAuditHistoryAsync(
        string callId, Guid userId, int limit, CancellationToken ct)
    {
        var events = await _auditViewer.GetForUserAsync(userId, limit, ct);

        // Render each event as a single line, substituting the viewer's GUID
        // with "You" and skipping events whose action has no verb mapping
        // (defensive — avoids dumping unstructured Description blobs into
        // agent context).
        var lines = events
            .Select(e => e.RenderPlainText(viewerUserId: userId))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        var content = lines.Count == 0
            ? "No audit history for this user."
            : string.Join('\n', lines);

        return new AnthropicToolResult(callId, content, IsError: false);
    }

    private static int ParseAuditHistoryLimit(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("limit", out var limitElem))
            return DefaultAuditHistoryLimit;
        if (limitElem.ValueKind != JsonValueKind.Number || !limitElem.TryGetInt32(out var requested))
            return DefaultAuditHistoryLimit;
        if (requested < 1)
            return 1;
        if (requested > MaxAuditHistoryLimit)
            return MaxAuditHistoryLimit;
        return requested;
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using Humans.Application.Constants;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Models;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Services.Preload;
using Microsoft.Extensions.Logging;
using NodaTime;

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
    private readonly IShiftSignupService _shiftSignups;
    private readonly IShiftManagementService _shiftManagement;
    private readonly ILogger<AgentToolDispatcher> _logger;

    public AgentToolDispatcher(
        AgentSectionDocReader sections,
        AgentFeatureSpecReader features,
        IAuditViewerService auditViewer,
        IShiftSignupService shiftSignups,
        IShiftManagementService shiftManagement,
        ILogger<AgentToolDispatcher> logger)
    {
        _sections = sections;
        _features = features;
        _auditViewer = auditViewer;
        _shiftSignups = shiftSignups;
        _shiftManagement = shiftManagement;
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
                case AgentToolNames.GetShiftDetails:
                    {
                        var shiftIdString = args.TryGetProperty("shiftId", out var sid) ? sid.GetString() ?? "" : "";
                        if (!Guid.TryParse(shiftIdString, out var shiftKey))
                            return new AnthropicToolResult(call.Id, "shiftId must be a valid GUID.", IsError: true);
                        return await DispatchGetShiftDetailsAsync(call.Id, userId, shiftKey, cancellationToken);
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

    /// <summary>
    /// Resolves the agent's <c>get_shift_details</c> argument — which is either
    /// a <see cref="ShiftSignup.SignupBlockId"/> (block) or a single
    /// <see cref="ShiftSignup.Id"/> — and returns a textualized summary of the
    /// matching signup(s). Only signups belonging to the calling user are
    /// reachable; anything else returns "Shift not found" (no information leak
    /// about other users' shifts).
    /// </summary>
    private async Task<AnthropicToolResult> DispatchGetShiftDetailsAsync(
        string callId, Guid userId, Guid shiftKey, CancellationToken ct)
    {
        var activeEvent = await _shiftManagement.GetActiveAsync();
        if (activeEvent is null)
            return new AnthropicToolResult(callId, "No active event configured.", IsError: true);

        var signups = await _shiftSignups.GetByUserAsync(userId, activeEvent.Id);

        // Try block first. Filter to active states so RenderShiftDetails
        // reports a status consistent with the snapshot tail (which also
        // filters to Pending/Confirmed). Without this, a block where day 1
        // was individually bailed but days 2–7 stayed Confirmed would render
        // "Status: Bailed" here while the snapshot showed "Confirmed".
        var blockMatches = signups
            .Where(s => s.SignupBlockId == shiftKey
                && s.Status is SignupStatus.Pending or SignupStatus.Confirmed)
            .ToList();
        if (blockMatches.Count > 0)
            return new AnthropicToolResult(callId,
                RenderShiftDetails(blockMatches, activeEvent), IsError: false);

        // Fall back to singleton id.
        var singleton = signups.FirstOrDefault(s => s.Id == shiftKey);
        if (singleton is not null)
            return new AnthropicToolResult(callId,
                RenderShiftDetails(new[] { singleton }, activeEvent), IsError: false);

        return new AnthropicToolResult(callId, "Shift not found.", IsError: true);
    }

    /// <summary>
    /// Builds the textual blob returned for <c>get_shift_details</c>. Renders
    /// the rota name, date span, status, day count, hours window, optional
    /// shift description, and Rota.PracticalInfo (where to show up). All
    /// signups passed in must belong to the calling user.
    /// </summary>
    private static string RenderShiftDetails(IReadOnlyList<ShiftSignup> signups, EventSettings ev)
    {
        // Order chronologically so first/last reflect actual span.
        var ordered = signups.OrderBy(s => s.Shift.DayOffset).ToList();
        var first = ordered[0];
        var last = ordered[^1];
        var rota = first.Shift.Rota;

        var startDate = ev.GateOpeningDate.PlusDays(first.Shift.DayOffset);
        var endDate = ev.GateOpeningDate.PlusDays(last.Shift.DayOffset);
        var dayCount = ordered.Select(s => s.Shift.DayOffset).Distinct().Count();
        var status = first.Status;
        var label = rota?.Name ?? "(unnamed rota)";

        var sb = new StringBuilder();
        if (dayCount > 1)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{label} — {FormatDate(startDate)} to {FormatDate(endDate)}"));
        }
        else
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{label} — {FormatDate(startDate)}"));
        }

        sb.AppendLine(dayCount > 1
            ? string.Create(CultureInfo.InvariantCulture, $"Status: {status} ({dayCount} days)")
            : string.Create(CultureInfo.InvariantCulture, $"Status: {status}"));

        // Hours window.
        if (first.Shift.IsAllDay)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"Hours: {Shift.AllDayWindowStart:HH:mm}–{Shift.AllDayWindowEnd:HH:mm} each day (all-day shift)"));
        }
        else
        {
            var totalHours = first.Shift.Duration.TotalHours;
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"Hours: starts {first.Shift.StartTime:HH:mm}, lasts {totalHours:0.##} hours"));
        }

        // Shift description (per-shift duties).
        if (!string.IsNullOrWhiteSpace(first.Shift.Description))
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Description: {first.Shift.Description.Trim()}"));

        // Rota PracticalInfo — the canonical "where to show up / what to bring" field.
        if (!string.IsNullOrWhiteSpace(rota?.PracticalInfo))
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Where to show up: {rota.PracticalInfo!.Trim()}"));

        if (!string.IsNullOrWhiteSpace(rota?.Description))
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Rota description: {rota.Description!.Trim()}"));

        return sb.ToString().TrimEnd();
    }

    private static string FormatDate(LocalDate date) =>
        date.ToString("uuuu-MM-dd", CultureInfo.InvariantCulture);

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

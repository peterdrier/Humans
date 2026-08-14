using System.Globalization;
using System.Text;
using System.Text.Json;
using Humans.Application.Constants;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Shifts.Contracts;
using Humans.Application.Models;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Agent.Services.Preload;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Agent.Services.Anthropic;

namespace Humans.Agent.Services;

internal sealed class AgentToolDispatcher(
    AgentSectionDocReader sections,
    AgentFeatureSpecReader features,
    CommunityFaqReader community,
    IAuditViewerService auditViewer,
    IShiftView shiftView,
    IBurnSettingsService burnSettings,
    ILogger<AgentToolDispatcher> logger) : IAgentToolDispatcher
{
    internal const int DefaultAuditHistoryLimit = 20;
    internal const int MaxAuditHistoryLimit = 50;

    public async Task<AnthropicToolResult> DispatchAsync(
        AnthropicToolCall call, Guid userId, CancellationToken cancellationToken)
    {
        if (!AgentToolNames.All.Contains(call.Name))
        {
            logger.LogWarning("Agent requested unknown tool {ToolName}", call.Name);
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
                        var body = await features.ReadAsync(name, cancellationToken);
                        return body is not null
                            ? new AnthropicToolResult(call.Id, body, IsError: false)
                            : UnknownKey(call.Id,
                                string.Create(CultureInfo.InvariantCulture, $"Feature spec not found: {name}."),
                                await features.KnownStemsAsync(cancellationToken));
                    }
                case AgentToolNames.FetchSectionGuide:
                    {
                        var key = args.TryGetProperty("section", out var s) ? s.GetString() ?? "" : "";
                        var body = await sections.ReadAsync(key, cancellationToken);
                        return body is not null
                            ? new AnthropicToolResult(call.Id, body, IsError: false)
                            : UnknownKey(call.Id,
                                string.Create(CultureInfo.InvariantCulture, $"Unknown section: {key}."),
                                sections.KnownSections);
                    }
                case AgentToolNames.FetchCommunityFaq:
                    {
                        var topic = args.TryGetProperty("topic", out var t) ? t.GetString() ?? "" : "";
                        var body = await community.ReadAsync(topic, cancellationToken);
                        return body is not null
                            ? new AnthropicToolResult(call.Id, CommunityFaqReader.WrapWithProvenance(body), IsError: false)
                            : UnknownKey(call.Id,
                                string.Create(CultureInfo.InvariantCulture, $"Unknown community FAQ topic: {topic}."),
                                (await community.ListTopicsAsync(cancellationToken)).Select(e => e.Topic));
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
            logger.LogWarning(ex, "Agent sent malformed JSON arguments for tool {ToolName}", call.Name);
            return new AnthropicToolResult(call.Id, "Malformed tool arguments (expected JSON object).", IsError: true);
        }
    }

    /// <summary>
    /// Builds the error a doc-fetch tool returns when the requested key misses. Naming the valid
    /// keys is the whole point: a bare "Unknown section: X" gives the model nothing to correct
    /// with, so it guesses again and often ends the turn with nothing to say
    /// (nobodies-collective/Humans#949). Falls back to the bare message when the key set could not
    /// be listed, so a GitHub outage never advertises an empty set as the accepted one.
    /// </summary>
    private static AnthropicToolResult UnknownKey(string callId, string message, IEnumerable<string> validKeys)
    {
        var keys = validKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        var content = keys.Count == 0
            ? message
            : string.Create(CultureInfo.InvariantCulture,
                $"{message} Valid keys are: {string.Join(", ", keys)}. Retry with one of these.");
        return new AnthropicToolResult(callId, content, IsError: true);
    }

    private async Task<AnthropicToolResult> DispatchGetAuditHistoryAsync(
        string callId, Guid userId, int limit, CancellationToken ct)
    {
        var events = await auditViewer.GetForUserAsync(userId, limit, ct);

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

    /// <summary>Resolves a SignupBlockId or Signup.Id (must belong to caller) and returns a textual summary.</summary>
    private async Task<AnthropicToolResult> DispatchGetShiftDetailsAsync(
        string callId, Guid userId, Guid shiftKey, CancellationToken ct)
    {
        var activeEvent = await burnSettings.GetActiveAsync(ct);
        if (activeEvent is null)
            return new AnthropicToolResult(callId, "No active event configured.", IsError: true);

        // T-09 (issue #720): read signups from the cached ShiftUserView
        // rather than IShiftSignupService.GetByUserAsync. The view already
        // filters Signups to the active event (see ShiftViewService).
        var userView = await shiftView.GetUserAsync(userId, ct);
        var signups = userView.Signups;

        // Try block first. Filter to active states so RenderShiftDetails
        // reports a status consistent with the snapshot tail (which also
        // filters to Pending/Confirmed). Without this, a block where day 1
        // was individually bailed but days 2–7 stayed Confirmed would render
        // "Status: Bailed" here while the snapshot showed "Confirmed".
        var blockMatches = signups
            .Where(s => s.SignupBlockId == shiftKey && s.IsActive)
            .ToList();
        if (blockMatches.Count > 0)
            return new AnthropicToolResult(callId,
                RenderShiftDetails(blockMatches, activeEvent), IsError: false);

        // Fall back to singleton id.
        var singleton = signups.FirstOrDefault(s => s.Id == shiftKey);
        if (singleton is not null)
            return new AnthropicToolResult(callId,
                RenderShiftDetails([singleton], activeEvent), IsError: false);

        return new AnthropicToolResult(callId, "Shift not found.", IsError: true);
    }

    /// <summary>Renders the get_shift_details blob. All signups passed in must belong to the caller.</summary>
    private static string RenderShiftDetails(IReadOnlyList<ShiftSignupSummary> signups, BurnSettingsInfo ev)
    {
        // Order chronologically so first/last reflect actual span.
        var ordered = signups.OrderBy(s => s.Date).ToList();
        var first = ordered[0];
        var last = ordered[^1];

        var startDate = first.Date;
        var endDate = last.Date;
        var dayCount = ordered.Select(s => s.Date).Distinct().Count();
        var status = first.Status;
        var label = string.IsNullOrWhiteSpace(first.RotaName) ? "(unnamed rota)" : first.RotaName;

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
        if (first.IsAllDay)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"Hours: {DateFormattingExtensions.TimeOfDayPattern.Format(first.WindowStart)}–{DateFormattingExtensions.TimeOfDayPattern.Format(first.WindowEnd)} each day (all-day shift)"));
        }
        else
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"Hours: starts {DateFormattingExtensions.TimeOfDayPattern.Format(first.WindowStart)}, lasts {first.DurationHours:0.##} hours"));
        }

        // Shift description (per-shift duties).
        if (!string.IsNullOrWhiteSpace(first.ShiftDescription))
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Description: {first.ShiftDescription.Trim()}"));

        // Rota PracticalInfo — the canonical "where to show up / what to bring" field.
        if (!string.IsNullOrWhiteSpace(first.RotaPracticalInfo))
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Where to show up: {first.RotaPracticalInfo.Trim()}"));

        if (!string.IsNullOrWhiteSpace(first.RotaDescription))
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Rota description: {first.RotaDescription.Trim()}"));

        return sb.ToString().TrimEnd();
    }

    private static string FormatDate(LocalDate date) =>
        date.ToInvariantDate();

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

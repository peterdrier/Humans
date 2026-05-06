using System.Globalization;
using System.Text;
using System.Text.Json;
using Humans.Application.Constants;
using Humans.Application.Interfaces;
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
    private readonly AgentSectionDocReader _sections;
    private readonly AgentFeatureSpecReader _features;
    private readonly IShiftSignupService _shiftSignups;
    private readonly IShiftManagementService _shiftManagement;
    private readonly IClock _clock;
    private readonly ILogger<AgentToolDispatcher> _logger;

    public AgentToolDispatcher(
        AgentSectionDocReader sections,
        AgentFeatureSpecReader features,
        IShiftSignupService shiftSignups,
        IShiftManagementService shiftManagement,
        IClock clock,
        ILogger<AgentToolDispatcher> logger)
    {
        _sections = sections;
        _features = features;
        _shiftSignups = shiftSignups;
        _shiftManagement = shiftManagement;
        _clock = clock;
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
                case AgentToolNames.GetShiftDetails:
                    {
                        var raw = args.TryGetProperty("shiftId", out var idElem) ? idElem.GetString() : null;
                        if (!Guid.TryParse(raw, out var shiftId))
                            return new AnthropicToolResult(call.Id, "Invalid shiftId argument (expected a UUID).", IsError: true);

                        return await DispatchGetShiftDetailsAsync(call.Id, userId, shiftId);
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

    private async Task<AnthropicToolResult> DispatchGetShiftDetailsAsync(
        string callId, Guid userId, Guid shiftId)
    {
        var eventSettings = await _shiftManagement.GetActiveAsync();
        if (eventSettings is null)
            return new AnthropicToolResult(callId, "No active event is currently configured.", IsError: true);

        // Pull the user's signups for the active event, then resolve the
        // supplied id against either SignupBlockId (range) or ShiftSignup.Id
        // (singleton). Filtering through the user's own signup list is the
        // privacy guard — anything that doesn't belong to the caller gets a
        // "not found" result.
        //
        // For multi-day blocks already in progress, filter to upcoming-only
        // days using the same `GetAbsoluteEnd > now` filter that
        // AgentUserSnapshotProvider applies when building UpcomingShifts —
        // otherwise the tool's day-count/date-range can disagree with the
        // row the user selected.
        var signups = await _shiftSignups.GetByUserAsync(userId, eventSettings.Id);
        var now = _clock.GetCurrentInstant();

        var blockMatches = signups
            .Where(s => s.SignupBlockId == shiftId && s.Shift.GetAbsoluteEnd(eventSettings) > now)
            .ToList();
        if (blockMatches.Count > 0)
            return new AnthropicToolResult(callId, RenderBlock(blockMatches, eventSettings), IsError: false);

        var singleton = signups.FirstOrDefault(s => s.Id == shiftId && s.Shift.GetAbsoluteEnd(eventSettings) > now);
        if (singleton is not null)
            return new AnthropicToolResult(callId, RenderSingleton(singleton, eventSettings), IsError: false);

        return new AnthropicToolResult(callId, "Shift not found.", IsError: true);
    }

    private static string RenderBlock(IReadOnlyList<ShiftSignup> blockSignups, EventSettings eventSettings)
    {
        var ordered = blockSignups.OrderBy(s => s.Shift.DayOffset).ToList();
        var first = ordered[0];
        var last = ordered[^1];
        var startDate = eventSettings.GateOpeningDate.PlusDays(first.Shift.DayOffset);
        var endDate = eventSettings.GateOpeningDate.PlusDays(last.Shift.DayOffset);
        var dayCount = ordered.Select(s => s.Shift.DayOffset).Distinct().Count();
        var status = ordered.Any(s => s.Status == SignupStatus.Pending)
            ? SignupStatus.Pending
            : SignupStatus.Confirmed;
        var rota = first.Shift.Rota;
        var allDay = ordered.All(s => s.Shift.IsAllDay);

        var sb = new StringBuilder();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"{rota.Name} — {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}{(allDay ? " (all-day)" : "")}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Status: {status} ({dayCount} days)"));
        AppendDescriptions(sb, first.Shift, rota);
        AppendHoursLine(sb, first.Shift, allDay);
        return sb.ToString().TrimEnd();
    }

    private static string RenderSingleton(ShiftSignup signup, EventSettings eventSettings)
    {
        var date = eventSettings.GateOpeningDate.PlusDays(signup.Shift.DayOffset);
        var rota = signup.Shift.Rota;
        var allDay = signup.Shift.IsAllDay;

        var sb = new StringBuilder();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"{rota.Name} — {date:yyyy-MM-dd}{(allDay ? " (all-day)" : "")}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Status: {signup.Status}"));
        AppendDescriptions(sb, signup.Shift, rota);
        AppendHoursLine(sb, signup.Shift, allDay);
        return sb.ToString().TrimEnd();
    }

    private static void AppendDescriptions(StringBuilder sb, Shift shift, Rota rota)
    {
        if (!string.IsNullOrWhiteSpace(shift.Description))
            sb.AppendLine("Description: " + shift.Description.Trim());
        if (!string.IsNullOrWhiteSpace(rota.Description))
            sb.AppendLine("Rota: " + rota.Description.Trim());
        // PracticalInfo is the closest thing today to a "where to show up"
        // field on Rota — meeting point, pre-shift instructions, what to
        // bring. A dedicated structured location field is out of scope per
        // the issue (file separately if needed).
        if (!string.IsNullOrWhiteSpace(rota.PracticalInfo))
            sb.AppendLine("Where to show up: " + rota.PracticalInfo.Trim());
    }

    private static void AppendHoursLine(StringBuilder sb, Shift shift, bool allDay)
    {
        if (allDay)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"Hours: {Shift.AllDayWindowStart:HH:mm}–{Shift.AllDayWindowEnd:HH:mm} each day (all-day shift)"));
        }
        else
        {
            var endTime = shift.StartTime.PlusMinutes((int)shift.Duration.TotalMinutes);
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"Hours: {shift.StartTime:HH:mm}–{endTime:HH:mm}"));
        }
    }
}

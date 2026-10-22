using System.Globalization;
using System.Text;
using System.Text.Json;
using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Agent.Services.Preload;
using NodaTime;
using Humans.Agent.Services.Anthropic;

namespace Humans.Agent.Services;

internal sealed class AgentToolDispatcher(
    AgentSectionDocReader sections,
    AgentFeatureSpecReader features,
    CommunityFaqReader community,
    IAuditViewerService auditViewer,
    IShiftView shiftView,
    ISettingsService settingsService,
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

        string lookup;
        var auditLimit = DefaultAuditHistoryLimit;
        try
        {
            using var doc = JsonDocument.Parse(call.JsonArguments);
            var args = doc.RootElement;
            if (args.ValueKind != JsonValueKind.Object)
                throw new JsonException("Tool arguments must be a JSON object.");
            lookup = call.Name switch
            {
                AgentToolNames.FetchFeatureSpec => ReadStringArgument(args, "name"),
                AgentToolNames.FetchSectionGuide => ReadStringArgument(args, "section"),
                AgentToolNames.FetchCommunityFaq => ReadStringArgument(args, "topic"),
                AgentToolNames.GetShiftDetails => ReadStringArgument(args, "shiftId"),
                _ => "",
            };
            if (string.Equals(call.Name, AgentToolNames.GetAuditHistory, StringComparison.Ordinal))
                auditLimit = ParseAuditHistoryLimit(args);
            if (string.Equals(call.Name, AgentToolNames.RouteToIssue, StringComparison.Ordinal))
            {
                // AgentService reads these strings to build the proposal after confirmation.
                _ = ReadStringArgument(args, "title");
                _ = ReadStringArgument(args, "description");
                _ = ReadStringArgument(args, "category");
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Agent sent malformed JSON arguments for tool {ToolName}: {Reason}", call.Name, ex.Message);
            return new AnthropicToolResult(call.Id, "Malformed tool arguments (expected JSON object).", IsError: true);
        }

        switch (call.Name)
        {
            case AgentToolNames.FetchFeatureSpec:
                {
                    var name = lookup;
                    var body = await features.ReadAsync(name, cancellationToken);
                    return body is not null
                        ? new AnthropicToolResult(call.Id, body, IsError: false)
                        : UnknownKey(call.Id,
                            string.Create(CultureInfo.InvariantCulture, $"Feature spec not found: {name}."),
                            await features.KnownStemsAsync(cancellationToken));
                }
            case AgentToolNames.FetchSectionGuide:
                {
                    var key = lookup;
                    var body = await sections.ReadAsync(key, cancellationToken);
                    return body is not null
                        ? new AnthropicToolResult(call.Id, body, IsError: false)
                        : UnknownKey(call.Id,
                            string.Create(CultureInfo.InvariantCulture, $"Unknown section: {key}."),
                            sections.KnownSections);
                }
            case AgentToolNames.FetchCommunityFaq:
                {
                    var topic = lookup;
                    var body = await community.ReadAsync(topic, cancellationToken);
                    return body is not null
                        ? new AnthropicToolResult(call.Id, CommunityFaqReader.WrapWithProvenance(body), IsError: false)
                        : UnknownKey(call.Id,
                            string.Create(CultureInfo.InvariantCulture, $"Unknown community FAQ topic: {topic}."),
                            (await community.ListTopicsAsync(cancellationToken)).Entries.Select(e => e.Topic));
                }
            case AgentToolNames.GetAuditHistory:
                {
                    return await DispatchGetAuditHistoryAsync(call.Id, userId, auditLimit, cancellationToken);
                }
            case AgentToolNames.GetShiftDetails:
                {
                    var shiftIdString = lookup;
                    if (!Guid.TryParse(shiftIdString, out var shiftKey))
                        return new AnthropicToolResult(call.Id, "shiftId must be a valid GUID.", IsError: true);
                    return await DispatchGetShiftDetailsAsync(call.Id, userId, shiftKey, cancellationToken);
                }
            case AgentToolNames.RouteToIssue:
                {
                    // No DB write: AgentService emits the proposal frame. This result just tells the model the turn
                    // is over.
                    return new AnthropicToolResult(call.Id,
                        "Proposal queued. The system will pre-fill an issue submission form for the user. Stop and await the next user turn.",
                        IsError: false);
                }
            default:
                return new AnthropicToolResult(call.Id, string.Create(CultureInfo.InvariantCulture, $"Tool dispatch not implemented: {call.Name}"), IsError: true);
        }
    }

    private static string ReadStringArgument(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return "";
        if (value.ValueKind != JsonValueKind.String)
            throw new JsonException($"Tool argument '{name}' must be a string.");
        return value.GetString() ?? "";
    }

    /// <summary>
    /// Naming the valid keys lets the model correct its call. Falls back to the bare message when the keys could not
    /// be listed, so a GitHub outage never advertises an empty set.
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

        // Substitute "You" for the viewer and skip actions with no verb mapping, rather than dumping Description blobs.
        var lines = events
            .Select(e => e.RenderPlainText(viewerUserId: userId))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        var content = lines.Count == 0
            ? "No audit history for this user."
            : string.Join('\n', lines);

        return new AnthropicToolResult(callId, content, IsError: false);
    }

    /// <summary>The signup must belong to the caller.</summary>
    private async Task<AnthropicToolResult> DispatchGetShiftDetailsAsync(
        string callId, Guid userId, Guid shiftKey, CancellationToken ct)
    {
        var activeEvent = await settingsService.GetActiveEventSettingsAsync(ct);
        if (activeEvent is null)
            return new AnthropicToolResult(callId, "No active event configured.", IsError: true);

        // The cached ShiftUserView already filters signups to the active event.
        var userView = await shiftView.GetUserAsync(userId, ct);
        var signups = userView.Signups;

        // Active states only, so the status agrees with the snapshot tail: a block whose day 1 was bailed would
        // otherwise render "Bailed" here and "Confirmed" there.
        var blockMatches = signups
            .Where(s => s.SignupBlockId == shiftKey && s.IsActive)
            .ToList();
        if (blockMatches.Count > 0)
            return new AnthropicToolResult(callId,
                RenderShiftDetails(blockMatches), IsError: false);

        var singleton = signups.FirstOrDefault(s => s.Id == shiftKey);
        if (singleton is not null)
            return new AnthropicToolResult(callId,
                RenderShiftDetails([singleton]), IsError: false);

        return new AnthropicToolResult(callId, "Shift not found.", IsError: true);
    }

    /// <summary>All signups passed in must belong to the caller.</summary>
    private static string RenderShiftDetails(IReadOnlyList<ShiftSignupSummary> signups)
    {
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

using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Shifts.Domain;
using NodaTime;
using Humans.Shifts.Data;
using Humans.Users.Contracts;

namespace Humans.Shifts.Services;

/// <summary>One audit row per dispatch; the recipient rows are auditable through the outbox.</summary>
internal sealed class RotaCoordinatorMessageService(
    IShiftManagementRepository repo,
    ITeamService teamService,
    IUserService userService,
    IEmailService emailService,
    ShiftsEmails emailMessages,
    IAuditLogService auditLogService,
    EventCalendarResolver calendarResolver,
    IClock clock,
    ILogger<RotaCoordinatorMessageService> logger) : IRotaCoordinatorMessageService
{
    public async Task<RotaMessageDispatchResult> SendRotaMessageAsync(
        Guid rotaId,
        Guid senderUserId,
        string messageText,
        bool includeShifts,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageText))
            return RotaMessageDispatchResult.Failure("Message body is required.");

        var rota = await repo.GetRotaAsync(rotaId, RotaReadShape.View, ct);
        if (rota is null)
            return RotaMessageDispatchResult.Failure("Rota not found.");

        var eventSettings = await calendarResolver.GetAsync(rota.EventSettingsId, ct)
            ?? throw new InvalidOperationException(
                $"Rota {rotaId}'s event calendar is not configured in Settings.");

        var signups = GetActiveSignups(rota);
        if (signups.Count == 0)
            return RotaMessageDispatchResult.Failure("This rota has no active signups to email.");

        var senderInfos = await userService.GetUserInfosAsync([senderUserId], ct);
        if (!senderInfos.TryGetValue(senderUserId, out var sender))
            return RotaMessageDispatchResult.Failure("Sender not found.");

        var groups = new[]
        {
            new RotaSignupGroup(rota.Id, rota.Name, eventSettings, signups)
        };

        var summary = await DispatchToRecipientsAsync(
            groups,
            buildRequest: (recipient, shiftGroups, language) => new CoordinatorRotaMessageRequest(
                RecipientUserId: recipient.Id,
                RecipientEmail: recipient.Email!,
                RecipientName: recipient.BurnerName,
                SenderName: sender.BurnerName,
                SenderEmail: sender.Email,
                RotaName: rota.Name,
                MessageText: messageText,
                // One rota, so exactly one group, or none when the coordinator left the shift list out.
                ShiftLines: shiftGroups.Count == 0 ? [] : shiftGroups[0].ShiftLines,
                IncludeShifts: includeShifts,
                Culture: language),
            enqueue: (req, token) => emailService.SendAsync(emailMessages.CoordinatorRotaMessage(req), token),
            logScope: ("rota", rota.Id.ToString()),
            includeShifts,
            ct);

        var auditSuffix = string.Empty;
        if (summary.Skipped > 0) auditSuffix += $" ({summary.Skipped} skipped: no email or missing user)";
        if (summary.Failed > 0) auditSuffix += $" ({summary.Failed} failed: enqueue error)";

        await AuditDispatchAsync(
            ShiftsAuditActions.CoordinatorRotaMessageSent,
            nameof(Rota), rota.Id,
            $"Requested rota message '{Truncate(messageText, 120)}' for {summary.Requested} recipient(s) on '{rota.Name}'"
                + auditSuffix,
            senderUserId, summary.Requested);

        // Every enqueue threw. The audit row already records it; fail so the controller shows no success toast.
        if (summary.Requested == 0 && summary.Failed > 0)
            return RotaMessageDispatchResult.Failure(
                $"Failed to enqueue any emails ({summary.Failed} enqueue error(s)); check server logs.");

        return RotaMessageDispatchResult.Success(summary.Requested, rota.Name);
    }

    public async Task<TeamRotasMessageDispatchResult> SendTeamRotasMessageAsync(
        Guid teamId,
        Guid senderUserId,
        string messageText,
        bool includeShifts,
        TeamRotasAudienceFilter filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (string.IsNullOrWhiteSpace(messageText))
            return TeamRotasMessageDispatchResult.Failure("Message body is required.");

        var team = await teamService.GetTeamAsync(teamId);
        if (team is null)
            return TeamRotasMessageDispatchResult.Failure("Team not found.");

        var groups = await BuildTeamRotaGroupsAsync(teamId, filter, ct);
        if (groups.Count == 0)
            return TeamRotasMessageDispatchResult.Failure(
                "No rota in this team matches that selection and has active signups to email.");

        var senderInfos = await userService.GetUserInfosAsync([senderUserId], ct);
        if (!senderInfos.TryGetValue(senderUserId, out var sender))
            return TeamRotasMessageDispatchResult.Failure("Sender not found.");

        var summary = await DispatchToRecipientsAsync(
            groups,
            buildRequest: (recipient, shiftGroups, language) => new CoordinatorTeamRotasMessageRequest(
                RecipientUserId: recipient.Id,
                RecipientEmail: recipient.Email!,
                RecipientName: recipient.BurnerName,
                SenderName: sender.BurnerName,
                SenderEmail: sender.Email,
                TeamName: team.Name,
                MessageText: messageText,
                ShiftGroups: shiftGroups,
                IncludeShifts: includeShifts,
                Culture: language),
            enqueue: (req, token) => emailService.SendAsync(emailMessages.CoordinatorTeamRotasMessage(req), token),
            logScope: ("team", teamId.ToString()),
            includeShifts,
            ct);

        var auditSuffix = string.Empty;
        if (summary.Skipped > 0) auditSuffix += $" ({summary.Skipped} skipped: no email or missing user)";
        if (summary.Failed > 0) auditSuffix += $" ({summary.Failed} failed: enqueue error)";

        await AuditDispatchAsync(
            ShiftsAuditActions.CoordinatorTeamRotasMessageSent,
            // Persisted literal: memory/code/type-name-as-persisted-string.md.
            "Team", teamId,
            $"Requested team-wide rota message '{Truncate(messageText, 120)}' for {summary.Requested} recipient(s) "
                + $"across {groups.Count} rota(s) in '{team.Name}'"
                + auditSuffix,
            senderUserId, summary.Requested);

        if (summary.Requested == 0 && summary.Failed > 0)
            return TeamRotasMessageDispatchResult.Failure(
                $"Failed to enqueue any emails ({summary.Failed} enqueue error(s)); check server logs.");

        return TeamRotasMessageDispatchResult.Success(summary.Requested, groups.Count, team.Name);
    }

    private async Task AuditDispatchAsync(
        AuditActionDefinition action, string entityType, Guid entityId, string description, Guid actorUserId, int requested)
    {
        try
        {
            await auditLogService.LogAsync(action, entityType, entityId, description, actorUserId);
        }
        catch (Exception ex) when (requested > 0)
        {
            // Retrying the dispatch would duplicate email requests already accepted by the outbox.
            logger.LogCritical(ex,
                "Coordinator message audit failed after {RequestedCount} successful email requests: {Action} on {EntityType} {EntityId}, sender {SenderUserId}",
                requested, action, entityType, entityId, actorUserId);
        }
    }

    public async Task<TeamRotasRecipientPreview> GetTeamRotasRecipientPreviewAsync(
        Guid teamId,
        TeamRotasAudienceFilter filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var groups = await BuildTeamRotaGroupsAsync(teamId, filter, ct);
        if (groups.Count == 0)
            return new TeamRotasRecipientPreview(0, []);

        var recipientIds = groups
            .SelectMany(g => g.Signups)
            .Select(s => s.UserId)
            .Distinct()
            .ToList();

        if (recipientIds.Count == 0)
            return new TeamRotasRecipientPreview(groups.Count, []);

        var infos = await userService.GetUserInfosAsync(recipientIds, ct);
        var names = recipientIds
            .Select(id => infos.TryGetValue(id, out var u) ? u.BurnerName : null)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList();

        return new TeamRotasRecipientPreview(groups.Count, names);
    }

    /// <summary>
    /// Under the default upcoming filter a rota counts while any of its shifts has not ended, so events mid-flight
    /// still count; otherwise the rota's period decides. Rotas with no active signups are left out.
    /// </summary>
    private async Task<IReadOnlyList<RotaSignupGroup>> BuildTeamRotaGroupsAsync(
        Guid teamId,
        TeamRotasAudienceFilter filter,
        CancellationToken ct)
    {
        var calendar = await calendarResolver.GetActiveAsync(ct);
        if (calendar is null) return [];

        var rotas = (await repo.GetRotasAsync(
                calendar.Id,
                [teamId],
                RotaReadShape.View,
                ct))
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
        if (rotas.Count == 0) return [];

        var now = clock.GetCurrentInstant();

        var groups = new List<RotaSignupGroup>(rotas.Count);
        foreach (var rota in rotas)
        {
            if (!filter.Includes(rota.Period)) continue;

            if (filter.UpcomingOnly && !rota.Shifts.Any(s => s.GetAbsoluteEnd(calendar) > now))
                continue;

            var activeSignups = GetActiveSignups(rota);

            if (activeSignups.Count == 0) continue;

            groups.Add(new RotaSignupGroup(rota.Id, rota.Name, calendar, activeSignups));
        }

        return groups;
    }

    private static IReadOnlyList<ShiftSignup> GetActiveSignups(Rota rota) =>
        rota.Shifts
            .SelectMany(shift => shift.ShiftSignups
                .Where(signup => signup.Status is SignupStatus.Pending or SignupStatus.Confirmed)
                .Select(signup =>
                {
                    // AsNoTracking eager loads do not reliably fix up reverse navs.
                    signup.Shift = shift;
                    return signup;
                }))
            .ToList();

    /// <summary>
    /// Shared by the per-rota and team paths: dedupes signups by user across rotas, formats each rota in its own
    /// timezone, and isolates per-recipient failures.
    /// </summary>
    private async Task<DispatchSummary> DispatchToRecipientsAsync<TRequest>(
        IReadOnlyList<RotaSignupGroup> rotaGroups,
        Func<UserInfo, IReadOnlyList<CoordinatorRotaShiftGroup>, string, TRequest> buildRequest,
        Func<TRequest, CancellationToken, Task> enqueue,
        (string Type, string Id) logScope,
        bool includeShifts,
        CancellationToken ct)
    {
        var byUser = new Dictionary<Guid, List<(RotaSignupGroup Group, ShiftSignup Signup)>>();
        foreach (var group in rotaGroups)
        {
            foreach (var signup in group.Signups)
            {
                if (!byUser.TryGetValue(signup.UserId, out var list))
                {
                    list = new List<(RotaSignupGroup, ShiftSignup)>();
                    byUser[signup.UserId] = list;
                }
                list.Add((group, signup));
            }
        }

        if (byUser.Count == 0)
            return new DispatchSummary(0, 0, 0);

        var recipientIds = byUser.Keys.ToList();
        var recipientInfos = await userService.GetUserInfosAsync(recipientIds, ct);

        var requested = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var (userId, entries) in byUser)
        {
            if (!recipientInfos.TryGetValue(userId, out var recipient))
            {
                logger.LogWarning(
                    "Skipping coordinator-message recipient {UserId} on {ScopeType} {ScopeId}: user not found",
                    userId, logScope.Type, logScope.Id);
                skipped++;
                continue;
            }

            var email = recipient.Email;
            if (string.IsNullOrWhiteSpace(email))
            {
                logger.LogWarning(
                    "Skipping coordinator-message recipient {UserId} on {ScopeType} {ScopeId}: no email address",
                    userId, logScope.Type, logScope.Id);
                skipped++;
                continue;
            }

            // The template drops the whole section rather than print a lead-in over an empty list.
            IReadOnlyList<CoordinatorRotaShiftGroup> shiftGroups = includeShifts
                ? entries
                    .GroupBy(e => e.Group.RotaId)
                    .Select(g =>
                    {
                        var group = g.First().Group;
                        var lines = BuildShiftLines(
                            g.Select(e => e.Signup).ToList(),
                            group.EventSettings);
                        return new CoordinatorRotaShiftGroup(group.RotaName, lines);
                    })
                    .OrderBy(g => g.RotaName, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];

            var language = recipient.PreferredLanguage.IsSupportedCultureCode()
                ? recipient.PreferredLanguage
                : CultureCatalog.DefaultCultureCode;
            var request = buildRequest(recipient, shiftGroups, language);

            // One recipient's outbox failure must not unwind the loop and leave the audit row unwritten.
            try
            {
                await enqueue(request, ct);
                // Email may suppress an opted-out recipient without throwing.
                requested++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogError(ex,
                    "Failed to enqueue coordinator-message email for recipient {UserId} on {ScopeType} {ScopeId}",
                    userId, logScope.Type, logScope.Id);
            }
        }

        return new DispatchSummary(requested, skipped, failed);
    }

    // Chronological, in the rota's timezone.
    private static IReadOnlyList<string> BuildShiftLines(
        IReadOnlyList<ShiftSignup> userSignups,
        Humans.Settings.Contracts.EventSettingsInfo eventSettings)
    {
        var tz = DateTimeZoneProviders.Tzdb[eventSettings.TimeZoneId];
        return userSignups
            .Select(s => new
            {
                s.Shift.IsAllDay,
                ZonedStart = s.Shift.GetAbsoluteStart(eventSettings).InZone(tz)
            })
            .OrderBy(x => x.ZonedStart.ToInstant())
            .Select(x => FormatShiftLine(x.ZonedStart, x.IsAllDay))
            .ToList();
    }

    private static string FormatShiftLine(ZonedDateTime zoned, bool isAllDay)
    {
        var local = zoned.LocalDateTime;
        // Invariant culture: stable parseable shape for short ops notices.
        return isAllDay
            ? DateFormattingExtensions.OpsNoticeDatePattern.Format(local.Date)
            : $"{DateFormattingExtensions.OpsNoticeDatePattern.Format(local.Date)} @ {DateFormattingExtensions.TimeOfDayPattern.Format(local.TimeOfDay)}";
    }

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;
        var length = max;
        if (char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
            length--;
        return text[..length] + "…";
    }

    private sealed record RotaSignupGroup(
        Guid RotaId,
        string RotaName,
        Humans.Settings.Contracts.EventSettingsInfo EventSettings,
        IReadOnlyList<ShiftSignup> Signups);

    private sealed record DispatchSummary(
        int Requested,
        int Skipped,
        int Failed);
}

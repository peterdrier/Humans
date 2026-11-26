using Humans.Base.Extensions;
using Humans.Audit.Contracts;
using Humans.Email.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Shifts.Domain;
using NodaTime;
using Humans.Shifts.Services.Dtos;
using Humans.Users.Contracts;

namespace Humans.Shifts.Services;

/// <summary>One audit row per dispatch; the recipient rows are auditable through the outbox.</summary>
internal sealed class RotaCoordinatorMessageService(
    IShiftRotaRows rotaRows,
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

        var row = (await rotaRows.GetRotaRowsAsync([rotaId], ct)).FirstOrDefault();
        if (row is null)
            return RotaMessageDispatchResult.Failure("Rota not found.");
        var rota = row.Rota;

        var eventSettings = await calendarResolver.GetAsync(rota.EventSettingsId, ct)
            ?? throw new InvalidOperationException(
                $"Rota {rotaId}'s event calendar is not configured in Settings.");

        var signups = GetActiveSignups(row);
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
                Shifts: shiftGroups.Count == 0 ? [] : shiftGroups[0].Shifts,
                IncludeShifts: includeShifts,
                Culture: language),
            enqueue: (req, token) => emailService.SendAsync(emailMessages.CoordinatorRotaMessage(req), token),
            logScope: ("rota", rota.Id.ToString()),
            includeShifts,
            ct);

        await AuditDispatchAsync(
            ShiftsAuditActions.CoordinatorRotaMessageSent,
            nameof(Rota), rota.Id,
            $"Requested rota message '{Truncate(messageText, 120)}' for {summary.Requested} recipient(s) on '{rota.Name}'"
                + summary.AuditSuffix,
            senderUserId, summary.Requested);

        if (summary.FailureMessage is { } failure)
            return RotaMessageDispatchResult.Failure(failure);

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

        var team = await teamService.GetTeamAsync(teamId, ct);
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

        await AuditDispatchAsync(
            ShiftsAuditActions.CoordinatorTeamRotasMessageSent,
            // Persisted literal: memory/code/type-name-as-persisted-string.md.
            "Team", teamId,
            $"Requested team-wide rota message '{Truncate(messageText, 120)}' for {summary.Requested} recipient(s) "
                + $"across {groups.Count} rota(s) in '{team.Name}'"
                + summary.AuditSuffix,
            senderUserId, summary.Requested);

        if (summary.FailureMessage is { } failure)
            return TeamRotasMessageDispatchResult.Failure(failure);

        return TeamRotasMessageDispatchResult.Success(summary.Requested, groups.Count, team.Name);
    }

    private async Task AuditDispatchAsync(
        AuditActionDefinition action, string subjectType, Guid subjectId, string description, Guid actorUserId, int requested)
    {
        try
        {
            await auditLogService.LogAsync(action, subjectType, subjectId, description, actorUserId);
        }
        catch (Exception ex) when (requested > 0)
        {
            // Retrying the dispatch would duplicate email requests already accepted by the outbox.
            logger.LogCritical(ex,
                "Coordinator message audit failed after {RequestedCount} successful email requests: {Action} on {SubjectType} {SubjectId}, sender {SenderUserId}",
                requested, action, subjectType, subjectId, actorUserId);
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

        var infos = GetAvailableRecipients(recipientIds, await userService.GetUserInfosAsync(recipientIds, ct));
        var names = infos.Values
            .DistinctBy(user => user.Id)
            .Select(user => user.BurnerName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList();

        return new TeamRotasRecipientPreview(groups.Count, names);
    }

    internal static IReadOnlyDictionary<Guid, UserInfo> GetAvailableRecipients(
        IEnumerable<Guid> userIds, IReadOnlyDictionary<Guid, UserInfo> infos) =>
        userIds.Distinct()
            .Where(id => infos.TryGetValue(id, out var user) && !user.IsTombstone
                && !string.IsNullOrWhiteSpace(user.Email))
            .ToDictionary(id => id, id => infos[id]);

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

        var rotas = (await rotaRows.GetRotaRowsAsync(ct: ct))
            .Where(r => r.Rota.EventSettingsId == calendar.Id && r.Rota.TeamId == teamId)
            .ToList();
        if (rotas.Count == 0) return [];

        var now = clock.GetCurrentInstant();

        var groups = new List<RotaSignupGroup>(rotas.Count);
        foreach (var row in rotas)
        {
            if (!filter.Includes(row.Rota.Period)) continue;

            if (filter.UpcomingOnly && !row.Shifts.Any(s => s.Shift.GetAbsoluteEnd(calendar) > now))
                continue;

            var activeSignups = GetActiveSignups(row);

            if (activeSignups.Count == 0) continue;

            groups.Add(new RotaSignupGroup(row.Rota.Id, row.Rota.Name, calendar, activeSignups));
        }

        return groups;
    }

    private static IReadOnlyList<ActiveSignup> GetActiveSignups(ShiftAdminRota row) =>
        row.Shifts
            .SelectMany(shift => shift.Signups
                .Where(signup => signup.Status is SignupStatus.Pending or SignupStatus.Confirmed)
                .Select(signup => new ActiveSignup(signup.UserId, shift.Shift)))
            .ToList();

    /// <summary>
    /// Shared by the per-rota and team paths: dedupes signups by live user across rotas, resolves starts in each rota's own
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
        var byUser = new Dictionary<Guid, List<(RotaSignupGroup Group, ActiveSignup Signup)>>();
        foreach (var group in rotaGroups)
        {
            foreach (var signup in group.Signups)
            {
                if (!byUser.TryGetValue(signup.UserId, out var list))
                {
                    list = new List<(RotaSignupGroup, ActiveSignup)>();
                    byUser[signup.UserId] = list;
                }
                list.Add((group, signup));
            }
        }

        if (byUser.Count == 0)
            return new DispatchSummary(0, 0, 0);

        var recipientIds = byUser.Keys;
        var recipientInfos = GetAvailableRecipients(recipientIds, await userService.GetUserInfosAsync(recipientIds, ct));

        var requested = 0;
        var skipped = byUser.Count - recipientInfos.Count;
        var failed = 0;
        if (skipped > 0)
            logger.LogWarning(
                "Skipping {SkippedCount} coordinator-message recipients on {ScopeType} {ScopeId}: unavailable user or no email address",
                skipped, logScope.Type, logScope.Id);

        foreach (var recipientEntries in byUser.Where(entry => recipientInfos.ContainsKey(entry.Key))
            .GroupBy(entry => recipientInfos[entry.Key].Id))
        {
            var userId = recipientEntries.Key;
            var recipient = recipientInfos[recipientEntries.First().Key];
            var entries = recipientEntries.SelectMany(entry => entry.Value);

            // The template drops the whole section rather than print a lead-in over an empty list.
            IReadOnlyList<CoordinatorRotaShiftGroup> shiftGroups = includeShifts
                ? entries
                    .GroupBy(e => e.Group.RotaId)
                    .Select(g =>
                    {
                        var group = g.First().Group;
                        var shifts = g.DistinctBy(e => e.Signup.Shift.Id).Select(e => (
                            Start: e.Signup.Shift.GetAbsoluteStart(group.EventSettings).InZone(group.EventSettings.TimeZone),
                            e.Signup.Shift.IsAllDay)).ToList();
                        return new CoordinatorRotaShiftGroup(group.RotaName, shifts);
                    })
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
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failed++;
                logger.LogError(ex,
                    "Failed to enqueue coordinator-message email for recipient {UserId} on {ScopeType} {ScopeId}",
                    userId, logScope.Type, logScope.Id);
            }
        }

        return new DispatchSummary(requested, skipped, failed);
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
        BurnSettingsInfo EventSettings,
        IReadOnlyList<ActiveSignup> Signups);

    private sealed record ActiveSignup(Guid UserId, ShiftInfo Shift);

    private sealed record DispatchSummary(
        int Requested,
        int Skipped,
        int Failed)
    {
        public string AuditSuffix =>
            (Skipped > 0 ? $" ({Skipped} skipped: unavailable user or no email)" : string.Empty)
            + (Failed > 0 ? $" ({Failed} failed: enqueue error)" : string.Empty);

        // A partial dispatch succeeded; only an all-failed dispatch must suppress the success toast.
        public string? FailureMessage => Requested == 0 && Failed > 0
            ? $"Failed to enqueue any emails ({Failed} enqueue error(s)); check server logs."
            : null;
    }
}

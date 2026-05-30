using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Text;

namespace Humans.Application.Services.Shifts;

/// <summary>
/// Groups active signups by user and enqueues one personalised email per recipient via the outbox.
/// One audit row per dispatch (recipient rows are auditable through the outbox).
/// </summary>
public sealed class RotaCoordinatorMessageService(
    IShiftSignupRepository signupRepo,
    IShiftManagementRepository mgmtRepo,
    ITeamServiceRead teamService,
    IUserServiceRead userService,
    IEmailService emailService,
    IEmailMessageFactory emailMessages,
    IAuditLogService auditLogService,
    IClock clock,
    ILogger<RotaCoordinatorMessageService> logger) : IRotaCoordinatorMessageService
{
    public async Task<RotaMessageDispatchResult> SendRotaMessageAsync(
        Guid rotaId,
        Guid senderUserId,
        string messageText,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageText))
            return RotaMessageDispatchResult.Failure("Message body is required.");

        var rota = await signupRepo.GetRotaWithShiftsAsync(rotaId, ct);
        if (rota is null)
            return RotaMessageDispatchResult.Failure("Rota not found.");

        var eventSettings = rota.EventSettings
            ?? throw new InvalidOperationException(
                $"Rota {rotaId} loaded without EventSettings — repository contract broken.");

        var signups = await signupRepo.GetActiveByRotaAsync(rotaId, ct);
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
            buildRequest: (recipient, shiftGroups) => new CoordinatorRotaMessageRequest(
                RecipientEmail: recipient.Email!,
                RecipientName: recipient.BurnerName,
                SenderName: sender.BurnerName,
                SenderEmail: sender.Email,
                RotaName: rota.Name,
                MessageText: messageText,
                // Per-rota body keeps the flat shift-list shape; there is exactly
                // one group in this path so the flatten is trivial.
                ShiftLines: shiftGroups[0].ShiftLines,
                Culture: recipient.PreferredLanguage),
            enqueue: (req, token) => emailService.SendAsync(emailMessages.CoordinatorRotaMessage(req), token),
            logScope: ("rota", rota.Id.ToString()),
            ct);

        var auditSuffix = string.Empty;
        if (summary.Skipped > 0) auditSuffix += $" ({summary.Skipped} skipped: no email or missing user)";
        if (summary.Failed > 0) auditSuffix += $" ({summary.Failed} failed: enqueue error)";

        await auditLogService.LogAsync(
            AuditAction.CoordinatorRotaMessageSent,
            nameof(Rota), rota.Id,
            $"Sent rota message '{Truncate(messageText, 120)}' to {summary.Queued} recipient(s) on '{rota.Name}'"
                + auditSuffix,
            senderUserId);

        // Total-failure path: every enqueue threw. Audit row already records
        // the failures; surface a Failure result so the controller does not
        // render a misleading "Queued 0 email(s)" success toast.
        if (summary.Queued == 0 && summary.Failed > 0)
            return RotaMessageDispatchResult.Failure(
                $"Failed to enqueue any emails ({summary.Failed} enqueue error(s)); check server logs.");

        return RotaMessageDispatchResult.Success(summary.Queued, rota.Name);
    }

    public async Task<TeamRotasMessageDispatchResult> SendTeamRotasMessageAsync(
        Guid teamId,
        Guid senderUserId,
        string messageText,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageText))
            return TeamRotasMessageDispatchResult.Failure("Message body is required.");

        var team = await teamService.GetTeamAsync(teamId);
        if (team is null)
            return TeamRotasMessageDispatchResult.Failure("Team not found.");

        var groups = await BuildTeamRotaGroupsAsync(teamId, ct);
        if (groups.Count == 0)
            return TeamRotasMessageDispatchResult.Failure(
                "This team has no upcoming rotas with active signups to email.");

        var senderInfos = await userService.GetUserInfosAsync([senderUserId], ct);
        if (!senderInfos.TryGetValue(senderUserId, out var sender))
            return TeamRotasMessageDispatchResult.Failure("Sender not found.");

        var summary = await DispatchToRecipientsAsync(
            groups,
            buildRequest: (recipient, shiftGroups) => new CoordinatorTeamRotasMessageRequest(
                RecipientEmail: recipient.Email!,
                RecipientName: recipient.BurnerName,
                SenderName: sender.BurnerName,
                SenderEmail: sender.Email,
                TeamName: team.Name,
                MessageText: messageText,
                ShiftGroups: shiftGroups,
                Culture: recipient.PreferredLanguage),
            enqueue: (req, token) => emailService.SendAsync(emailMessages.CoordinatorTeamRotasMessage(req), token),
            logScope: ("team", teamId.ToString()),
            ct);

        var auditSuffix = string.Empty;
        if (summary.Skipped > 0) auditSuffix += $" ({summary.Skipped} skipped: no email or missing user)";
        if (summary.Failed > 0) auditSuffix += $" ({summary.Failed} failed: enqueue error)";

        await auditLogService.LogAsync(
            AuditAction.CoordinatorTeamRotasMessageSent,
            nameof(Team), teamId,
            $"Sent team-wide rota message '{Truncate(messageText, 120)}' to {summary.Queued} recipient(s) "
                + $"across {groups.Count} rota(s) in '{team.Name}'"
                + auditSuffix,
            senderUserId);

        if (summary.Queued == 0 && summary.Failed > 0)
            return TeamRotasMessageDispatchResult.Failure(
                $"Failed to enqueue any emails ({summary.Failed} enqueue error(s)); check server logs.");

        return TeamRotasMessageDispatchResult.Success(summary.Queued, groups.Count, team.Name);
    }

    public async Task<TeamRotasRecipientPreview> GetTeamRotasRecipientPreviewAsync(
        Guid teamId,
        CancellationToken ct = default)
    {
        var groups = await BuildTeamRotaGroupsAsync(teamId, ct);
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
    /// Loads the team's current/upcoming rotas in the active event, paired with
    /// their active (Pending/Confirmed) signups. A rota is included if any of
    /// its shifts has not yet ended (so events mid-flight still count). Returns
    /// an empty list if no active event, the team has no rotas in it, or no
    /// rota in the team has any future shifts with active signups.
    /// </summary>
    private async Task<IReadOnlyList<RotaSignupGroup>> BuildTeamRotaGroupsAsync(
        Guid teamId,
        CancellationToken ct)
    {
        var eventSettings = await mgmtRepo.GetActiveEventSettingsAsync(ct);
        if (eventSettings is null) return [];

        var rotas = await mgmtRepo.GetRotasByDepartmentAsync(teamId, eventSettings.Id, ct);
        if (rotas.Count == 0) return [];

        var now = clock.GetCurrentInstant();

        var groups = new List<RotaSignupGroup>(rotas.Count);
        foreach (var rota in rotas)
        {
            // Per-rota EventSettings carries the timezone — the eager-load on
            // GetRotasByDepartmentAsync attached it; keep the reference for
            // downstream shift-line formatting.
            var rotaEs = rota.EventSettings
                ?? throw new InvalidOperationException(
                    $"Rota {rota.Id} loaded without EventSettings — repository contract broken.");

            var hasFutureShift = rota.Shifts.Any(s => s.GetAbsoluteEnd(rotaEs) > now);
            if (!hasFutureShift) continue;

            // ShiftSignup.Shift may not be populated when reached via the
            // Shift.ShiftSignups eager-load (AsNoTracking skips reverse-nav
            // fix-up); set it explicitly so the shift-line formatter can read
            // IsAllDay/StartTime/Duration without another DB round-trip.
            var activeSignups = rota.Shifts
                .SelectMany(s => s.ShiftSignups
                    .Where(ss => ss.Status is SignupStatus.Pending or SignupStatus.Confirmed)
                    .Select(ss => { ss.Shift = s; return ss; }))
                .ToList();

            if (activeSignups.Count == 0) continue;

            groups.Add(new RotaSignupGroup(rota.Id, rota.Name, rotaEs, activeSignups));
        }

        return groups;
    }

    /// <summary>
    /// Generic per-recipient dispatch loop shared by the per-rota and team-level
    /// paths. Groups signups by user (dedupe across rotas), looks up recipients,
    /// builds per-recipient shift-group lists in each rota's timezone, then
    /// invokes the caller's <paramref name="buildRequest"/> + <paramref name="enqueue"/>.
    /// Per-recipient failures isolated; aggregate counts returned for audit.
    /// </summary>
    private async Task<DispatchSummary> DispatchToRecipientsAsync<TRequest>(
        IReadOnlyList<RotaSignupGroup> rotaGroups,
        Func<UserInfo, IReadOnlyList<RotaShiftGroup>, TRequest> buildRequest,
        Func<TRequest, CancellationToken, Task> enqueue,
        (string Type, string Id) logScope,
        CancellationToken ct)
    {
        // userId -> list of (group, signup) so we can later partition per recipient per rota.
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

        var queued = 0;
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

            var shiftGroups = entries
                .GroupBy(e => e.Group.RotaId)
                .Select(g =>
                {
                    var group = g.First().Group;
                    var lines = BuildShiftLines(
                        g.Select(e => e.Signup).ToList(),
                        group.EventSettings);
                    return new RotaShiftGroup(group.RotaName, lines);
                })
                .OrderBy(g => g.RotaName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var request = buildRequest(recipient, shiftGroups);

            // Per-recipient enqueue is isolated: a transient outbox-write
            // failure on one recipient must not unwind the loop and leave
            // the audit row unwritten or earlier enqueues unaccounted for.
            try
            {
                await enqueue(request, ct);
                queued++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogError(ex,
                    "Failed to enqueue coordinator-message email for recipient {UserId} on {ScopeType} {ScopeId}",
                    userId, logScope.Type, logScope.Id);
            }
        }

        return new DispatchSummary(queued, skipped, failed);
    }

    // Chronologically ordered "ddd MMMM d [@ HH:mm]" lines in the rota's timezone.
    private static IReadOnlyList<string> BuildShiftLines(
        IReadOnlyList<ShiftSignup> userSignups,
        EventSettings eventSettings)
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
        var datePattern = LocalDateTimePattern.CreateWithInvariantCulture("ddd MMMM d");
        var timePattern = LocalDateTimePattern.CreateWithInvariantCulture("HH:mm");
        return isAllDay
            ? datePattern.Format(local)
            : $"{datePattern.Format(local)} @ {timePattern.Format(local)}";
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private sealed record RotaSignupGroup(
        Guid RotaId,
        string RotaName,
        EventSettings EventSettings,
        IReadOnlyList<ShiftSignup> Signups);

    private sealed record DispatchSummary(
        int Queued,
        int Skipped,
        int Failed);
}

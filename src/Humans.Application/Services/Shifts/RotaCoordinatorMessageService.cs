using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
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
    IUserService userService,
    IEmailService emailService,
    IAuditLogService auditLogService,
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

        var byUser = signups
            .GroupBy(s => s.UserId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var senderInfos = await userService.GetUserInfosAsync([senderUserId], ct);
        if (!senderInfos.TryGetValue(senderUserId, out var sender))
            return RotaMessageDispatchResult.Failure("Sender not found.");

        var recipientIds = byUser.Keys.ToList();
        var recipientInfos = await userService.GetUserInfosAsync(recipientIds, ct);

        var queued = 0;
        var skipped = 0;
        var failed = 0;
        foreach (var (userId, userSignups) in byUser)
        {
            if (!recipientInfos.TryGetValue(userId, out var recipient))
            {
                logger.LogWarning(
                    "Skipping rota-email recipient {UserId} on rota {RotaId}: user not found",
                    userId, rotaId);
                skipped++;
                continue;
            }

            var email = recipient.Email;
            if (string.IsNullOrWhiteSpace(email))
            {
                logger.LogWarning(
                    "Skipping rota-email recipient {UserId} on rota {RotaId}: no email address",
                    userId, rotaId);
                skipped++;
                continue;
            }

            var shiftLines = BuildShiftLines(userSignups, eventSettings);

            var request = new CoordinatorRotaMessageRequest(
                RecipientEmail: email,
                RecipientName: recipient.BurnerName,
                SenderName: sender.BurnerName,
                SenderEmail: sender.Email,
                RotaName: rota.Name,
                MessageText: messageText,
                ShiftLines: shiftLines,
                Culture: recipient.PreferredLanguage);

            // Per-recipient enqueue is isolated: a transient outbox-write
            // failure on one recipient must not unwind the loop and leave
            // the audit row unwritten or earlier enqueues unaccounted for.
            // Matches the fan-out pattern in AttendeeContactImportService.
            try
            {
                await emailService.SendCoordinatorRotaMessageAsync(request, ct);
                queued++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogError(ex,
                    "Failed to enqueue rota email for recipient {UserId} on rota {RotaId}",
                    userId, rotaId);
            }
        }

        var auditSuffix = string.Empty;
        if (skipped > 0) auditSuffix += $" ({skipped} skipped: no email or missing user)";
        if (failed > 0) auditSuffix += $" ({failed} failed: enqueue error)";

        await auditLogService.LogAsync(
            AuditAction.CoordinatorRotaMessageSent,
            nameof(Rota), rota.Id,
            $"Sent rota message '{Truncate(messageText, 120)}' to {queued} recipient(s) on '{rota.Name}'"
                + auditSuffix,
            senderUserId);

        // Total-failure path: every enqueue threw. Audit row already records
        // the failures; surface a Failure result so the controller does not
        // render a misleading "Queued 0 email(s)" success toast.
        if (queued == 0 && failed > 0)
            return RotaMessageDispatchResult.Failure(
                $"Failed to enqueue any emails ({failed} enqueue error(s)); check server logs.");

        return RotaMessageDispatchResult.Success(queued, rota.Name);
    }

    // Chronologically ordered "ddd MMMM d [@ HH:mm]" lines in the event's timezone.
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
}

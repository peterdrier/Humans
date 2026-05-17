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
/// Application-layer implementation of <see cref="IRotaCoordinatorMessageService"/>.
/// Enumerates current Pending/Confirmed signups on a rota, groups them by user,
/// and enqueues one personalised email per recipient via <see cref="IEmailService"/>.
/// </summary>
/// <remarks>
/// <para>
/// Reuses the existing outbox path so logging, opt-out routing, and category
/// suppression stay consistent with every other transactional send. The
/// per-recipient shift list is computed here from <see cref="Shift.GetAbsoluteStart"/>
/// in the event's timezone — same convention the rota detail page uses.
/// </para>
/// <para>
/// One audit row per send (per rota dispatch). Per-recipient email rows are
/// already auditable through the outbox table — no need to fan out audit log
/// entries per recipient.
/// </para>
/// </remarks>
public sealed class RotaCoordinatorMessageService : IRotaCoordinatorMessageService
{
    private readonly IShiftSignupRepository _signupRepo;
    private readonly IUserService _userService;
    private readonly IEmailService _emailService;
    private readonly IAuditLogService _auditLogService;
    private readonly ILogger<RotaCoordinatorMessageService> _logger;

    public RotaCoordinatorMessageService(
        IShiftSignupRepository signupRepo,
        IUserService userService,
        IEmailService emailService,
        IAuditLogService auditLogService,
        ILogger<RotaCoordinatorMessageService> logger)
    {
        _signupRepo = signupRepo;
        _userService = userService;
        _emailService = emailService;
        _auditLogService = auditLogService;
        _logger = logger;
    }

    public async Task<RotaMessageDispatchResult> SendRotaMessageAsync(
        Guid rotaId,
        Guid senderUserId,
        string messageText,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageText))
            return RotaMessageDispatchResult.Failure("Message body is required.");

        var rota = await _signupRepo.GetRotaWithShiftsAsync(rotaId, ct);
        if (rota is null)
            return RotaMessageDispatchResult.Failure("Rota not found.");

        var eventSettings = rota.EventSettings
            ?? throw new InvalidOperationException(
                $"Rota {rotaId} loaded without EventSettings — repository contract broken.");

        var signups = await _signupRepo.GetActiveByRotaAsync(rotaId, ct);
        if (signups.Count == 0)
            return RotaMessageDispatchResult.Failure("This rota has no active signups to email.");

        var byUser = signups
            .GroupBy(s => s.UserId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var senderInfos = await _userService.GetUserInfosAsync([senderUserId], ct);
        if (!senderInfos.TryGetValue(senderUserId, out var sender))
            return RotaMessageDispatchResult.Failure("Sender not found.");

        var recipientIds = byUser.Keys.ToList();
        var recipientInfos = await _userService.GetUserInfosAsync(recipientIds, ct);

        var queued = 0;
        var skipped = 0;
        foreach (var (userId, userSignups) in byUser)
        {
            if (!recipientInfos.TryGetValue(userId, out var recipient))
            {
                _logger.LogWarning(
                    "Skipping rota-email recipient {UserId} on rota {RotaId}: user not found",
                    userId, rotaId);
                skipped++;
                continue;
            }

            var email = recipient.Email;
            if (string.IsNullOrWhiteSpace(email))
            {
                _logger.LogWarning(
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

            await _emailService.SendCoordinatorRotaMessageAsync(request, ct);
            queued++;
        }

        await _auditLogService.LogAsync(
            AuditAction.CoordinatorRotaMessageSent,
            nameof(Rota), rota.Id,
            $"Sent rota message '{Truncate(messageText, 120)}' to {queued} recipient(s) on '{rota.Name}'"
                + (skipped > 0 ? $" ({skipped} skipped: no email or missing user)" : string.Empty),
            senderUserId);

        return RotaMessageDispatchResult.Success(queued, rota.Name);
    }

    /// <summary>
    /// Builds a single recipient's chronologically ordered shift label list.
    /// Format: <c>"ddd MMMM d"</c> for all-day, <c>"ddd MMMM d @ HH:mm"</c> for
    /// time-slotted. Uses the event's timezone, matching the rota page convention.
    /// </summary>
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
        // Invariant-culture day-and-month so the email body has a stable, parseable shape;
        // recipient-locale formatting would require per-recipient pattern selection and
        // doesn't materially help operational legibility (these messages are short ops notices).
        var datePattern = LocalDateTimePattern.CreateWithInvariantCulture("ddd MMMM d");
        var timePattern = LocalDateTimePattern.CreateWithInvariantCulture("HH:mm");
        return isAllDay
            ? datePattern.Format(local)
            : $"{datePattern.Format(local)} @ {timePattern.Format(local)}";
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}

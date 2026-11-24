using NodaTime;
using System.Globalization;
using System.Net;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Shifts.Services;

/// <summary>
/// Pure: no I/O. Copy renders in the recipient's culture inside a <see cref="CultureScope"/>
/// (memory/architecture/email-templates-live-in-sender.md).
/// </summary>
internal sealed class ShiftsEmails(
    IStringLocalizer<ShiftsResource> localizer,
    ILogger<ShiftsEmails> logger)
{
    public EmailMessage CoordinatorRotaMessage(CoordinatorRotaMessageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Shifts is null) throw new ArgumentNullException(nameof(request), "Shifts must not be null.");
        return Localized(request.Culture, () =>
        {
            var sanitizedMessage = SanitizedMarkdownRenderer.Render(request.MessageText);

            var shiftListHtml = request.Shifts.Count == 0
                ? NoShifts()
                : ShiftListHtml(request.Shifts);
            var shiftSectionHtml = ShiftSection(
                request.IncludeShifts, "Shifts_Email_CoordinatorRotaMessage_ShiftsIntro", shiftListHtml);

            return new EmailMessage(request.RecipientEmail, request.RecipientName, request.RecipientUserId,
                Lf("Shifts_Email_CoordinatorRotaMessage_Subject", request.RotaName),
                Lf("Shifts_Email_CoordinatorRotaMessage_Body",
                    Encode(request.RecipientName),
                    Encode(request.SenderName),
                    Encode(request.RotaName),
                    sanitizedMessage,
                    shiftSectionHtml,
                    SenderLine(request.SenderName, request.SenderEmail)),
                "coordinator_rota_message", MessageCategory.VolunteerUpdates,
                ReplyTo: request.SenderEmail);
        });
    }

    public EmailMessage CoordinatorTeamRotasMessage(CoordinatorTeamRotasMessageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ShiftGroups is null) throw new ArgumentNullException(nameof(request), "ShiftGroups must not be null.");
        return Localized(request.Culture, () =>
        {
            var sanitizedMessage = SanitizedMarkdownRenderer.Render(request.MessageText);

            // Empty groups fall back as the per-rota template does.
            string shiftGroupsHtml;
            if (request.ShiftGroups.Count == 0 || request.ShiftGroups.All(g => g.Shifts.Count == 0))
            {
                shiftGroupsHtml = NoShifts();
            }
            else
            {
                shiftGroupsHtml = string.Concat(request.ShiftGroups
                    .OrderBy(g => g.RotaName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(g => g.RotaName, StringComparer.Ordinal)
                    .Select(g =>
                {
                    var lines = g.Shifts.Count == 0
                        ? string.Empty
                        : ShiftListHtml(g.Shifts);
                    return $"<p><strong>{Encode(g.RotaName)}</strong></p>{lines}";
                }));
            }

            shiftGroupsHtml = ShiftSection(
                request.IncludeShifts, "Shifts_Email_CoordinatorTeamRotasMessage_ShiftsIntro", shiftGroupsHtml);

            return new EmailMessage(request.RecipientEmail, request.RecipientName, request.RecipientUserId,
                Lf("Shifts_Email_CoordinatorTeamRotasMessage_Subject", request.TeamName),
                Lf("Shifts_Email_CoordinatorTeamRotasMessage_Body",
                    Encode(request.RecipientName),
                    Encode(request.SenderName),
                    Encode(request.TeamName),
                    sanitizedMessage,
                    shiftGroupsHtml,
                    SenderLine(request.SenderName, request.SenderEmail)),
                "coordinator_team_rotas_message", MessageCategory.VolunteerUpdates,
                ReplyTo: request.SenderEmail);
        });
    }

    /// <summary>Nothing when the shift list is opted out: a bare lead-in over no list reads worse than no section.</summary>
    private string ShiftSection(bool include, string introKey, string listHtml) =>
        include ? $"<p>{Encode(L(introKey))}</p>{listHtml}" : string.Empty;

    private static string ShiftListHtml(IReadOnlyList<(ZonedDateTime Start, bool IsAllDay)> shifts) =>
        "<ul>" + string.Concat(shifts.OrderBy(shift => shift.Start.ToInstant()).Select(shift =>
        {
            var local = shift.Start.LocalDateTime;
            // Invariant culture: stable parseable shape for short ops notices.
            var date = DateFormattingExtensions.OpsNoticeDatePattern.Format(local.Date);
            var line = shift.IsAllDay
                ? date
                : $"{date} @ {DateFormattingExtensions.TimeOfDayPattern.Format(local.TimeOfDay)}";
            return $"<li>{Encode(line)}</li>";
        })) + "</ul>";

    private string NoShifts() =>
        $"<p><em>{Encode(L("Shifts_Email_CoordinatorRotaMessage_NoShifts"))}</em></p>";

    /// <summary>A mailto link when the coordinator shared an address.</summary>
    private static string SenderLine(string senderName, string? senderEmail) =>
        !string.IsNullOrEmpty(senderEmail)
            ? $"<p><strong>{Encode(senderName)}</strong> &mdash; <a href=\"mailto:{Encode(senderEmail)}\">{Encode(senderEmail)}</a></p>"
            : $"<p><strong>{Encode(senderName)}</strong></p>";

    private string L(string key) => localizer[key].Value;

    private string Lf(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, localizer[key].Value, args);

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private EmailMessage Localized(string? culture, Func<EmailMessage> build)
    {
        using (new CultureScope(culture, logger))
        {
            return build() with { Culture = CultureInfo.CurrentUICulture.Name };
        }
    }
}

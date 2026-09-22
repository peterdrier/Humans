using System.Globalization;
using System.Net;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Shifts.Services;

/// <summary>
/// Shifts' own email templates: one method per template, each returning a ready
/// <see cref="EmailMessage"/> (content plus the routing policy Shifts chooses — template
/// name, opt-out category and the coordinator reply-to) for the single
/// <see cref="IEmailService.SendAsync"/> path. Copy lives in Shifts' own resx set and is
/// rendered in the recipient's culture inside a <see cref="CultureScope"/>
/// (memory/architecture/email-templates-live-in-sender.md, peterdrier/Humans#1651).
/// Pure — no I/O, no persistence.
/// </summary>
internal sealed class ShiftsEmails(
    IStringLocalizer<ShiftsResource> localizer,
    ILogger<ShiftsEmails> logger)
{
    public EmailMessage CoordinatorRotaMessage(CoordinatorRotaMessageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ShiftLines is null) throw new ArgumentNullException(nameof(request), "ShiftLines must not be null.");
        return Localized(request.Culture, () =>
        {
            var sanitizedMessage = SanitizedMarkdownRenderer.Render(request.MessageText);

            var shiftListHtml = request.ShiftLines.Count == 0
                ? NoShifts()
                : "<ul>" + string.Concat(request.ShiftLines.Select(line => $"<li>{Encode(line)}</li>")) + "</ul>";
            var shiftSectionHtml = ShiftSection(
                request.IncludeShifts, "Shifts_Email_CoordinatorRotaMessage_ShiftsIntro", shiftListHtml);

            return new EmailMessage(request.RecipientEmail, request.RecipientName,
                Lf("Shifts_Email_CoordinatorRotaMessage_Subject", Encode(request.RotaName)),
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

            // Per-rota groups: bold rota name then <ul><li> shift lines.
            // Empty-groups fallback mirrors the per-rota template.
            string shiftGroupsHtml;
            if (request.ShiftGroups.Count == 0 || request.ShiftGroups.All(g => g.ShiftLines.Count == 0))
            {
                shiftGroupsHtml = NoShifts();
            }
            else
            {
                shiftGroupsHtml = string.Concat(request.ShiftGroups.Select(g =>
                {
                    var lines = g.ShiftLines.Count == 0
                        ? string.Empty
                        : "<ul>" + string.Concat(g.ShiftLines.Select(line => $"<li>{Encode(line)}</li>")) + "</ul>";
                    return $"<p><strong>{Encode(g.RotaName)}</strong></p>{lines}";
                }));
            }

            shiftGroupsHtml = ShiftSection(
                request.IncludeShifts, "Shifts_Email_CoordinatorTeamRotasMessage_ShiftsIntro", shiftGroupsHtml);

            return new EmailMessage(request.RecipientEmail, request.RecipientName,
                Lf("Shifts_Email_CoordinatorTeamRotasMessage_Subject", Encode(request.TeamName)),
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

    /// <summary>
    /// The body's shift block: its lead-in plus the pre-rendered list, or nothing at
    /// all when the coordinator opted the shift list out — a bare lead-in over no list
    /// would read worse than no section.
    /// </summary>
    private string ShiftSection(bool include, string introKey, string listHtml) =>
        include ? $"<p>{Encode(L(introKey))}</p>{listHtml}" : string.Empty;

    private string NoShifts() =>
        $"<p><em>{Encode(L("Shifts_Email_CoordinatorRotaMessage_NoShifts"))}</em></p>";

    /// <summary>The coordinator's sign-off, a mailto link when they shared an address.</summary>
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
            return build();
        }
    }
}

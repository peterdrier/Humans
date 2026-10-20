using System.Globalization;
using System.Net;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Finance.Services;

/// <summary>
/// Finance's own email templates: one method per template, each returning a ready
/// <see cref="EmailMessage"/> (content plus the routing policy Finance chooses —
/// template name and opt-out category) for the single
/// <see cref="IEmailService.SendAsync"/> path. Copy lives in Finance's own resx set
/// and is rendered in the recipient's culture inside a <see cref="CultureScope"/>
/// (memory/architecture/email-templates-live-in-sender.md, peterdrier/Humans#1651).
/// Pure — no I/O, no persistence.
/// </summary>
internal sealed class FinanceEmails(
    IStringLocalizer<FinanceResource> localizer,
    ILogger<FinanceEmails> logger)
{
    /// <summary>
    /// A SEPA payout file naming this member was generated (peterdrier/Humans#1820).
    /// <paramref name="ibanMasked"/> is the transfer's already-masked IBAN — the same string the
    /// audit line and every screen carry; the raw one goes nowhere but the file and its row.
    /// </summary>
    public EmailMessage SepaPayoutGenerated(
        string userEmail, string userName, decimal amount, string ibanMasked, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            userEmail, userName,
            L("Finance_Email_SepaPayoutGenerated_Subject"),
            Lf("Finance_Email_SepaPayoutGenerated_Body", Encode(userName), amount.ToEuro(), Encode(ibanMasked)),
            "sepa_payout_generated", MessageCategory.System));

    /// <summary>
    /// A transfer announced by <see cref="SepaPayoutGenerated"/> was cancelled at the bank before
    /// it executed and withdrawn from its file. <paramref name="announcedOn"/> is the file's
    /// generation day in the organisation's zone, rendered in the recipient's culture.
    /// </summary>
    public EmailMessage SepaPayoutWithdrawn(
        string userEmail, string userName, decimal amount, string ibanMasked, LocalDate announcedOn,
        string? culture = null)
        => Localized(culture, () => new EmailMessage(
            userEmail, userName,
            L("Finance_Email_SepaPayoutWithdrawn_Subject"),
            Lf("Finance_Email_SepaPayoutWithdrawn_Body", Encode(userName), amount.ToEuro(), Encode(ibanMasked),
                announcedOn.ToString("d", CultureInfo.CurrentCulture)),
            "sepa_payout_withdrawn", MessageCategory.System));

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

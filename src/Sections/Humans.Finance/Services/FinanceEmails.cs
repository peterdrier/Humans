using System.Globalization;
using System.Net;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;

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
            Lf("Finance_Email_SepaPayoutGenerated_Body", Encode(userName), Euros(amount), Encode(ibanMasked)),
            "sepa_payout_generated", MessageCategory.System));

    /// <summary>Amount in the recipient's own number format — "1.234,56 €" in es, "1,234.56 €" in en.</summary>
    private static string Euros(decimal amount) =>
        amount.ToString("N2", CultureInfo.CurrentCulture) + " €";

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

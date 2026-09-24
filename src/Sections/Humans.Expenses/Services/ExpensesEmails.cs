using System.Globalization;
using System.Net;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Humans.Expenses.Services;

/// <summary>
/// Expenses' own email templates: one method per template, each returning a ready
/// <see cref="EmailMessage"/> (content plus the routing policy Expenses chooses —
/// template name and opt-out category) for the single
/// <see cref="IEmailService.SendAsync"/> path. Copy lives in Expenses' own resx set
/// and is rendered in the recipient's culture inside a <see cref="CultureScope"/>
/// (memory/architecture/email-templates-live-in-sender.md, peterdrier/Humans#1651).
/// Pure — no I/O, no persistence.
/// </summary>
internal sealed class ExpensesEmails(
    IOptions<EmailSettings> settings,
    IStringLocalizer<ExpensesResource> localizer,
    ILogger<ExpensesEmails> logger)
{
    private readonly EmailSettings _settings = settings.Value;

    /// <summary>
    /// Finance approved the member's report (peterdrier/Humans#1820). <paramref name="payable"/>
    /// is what will actually be reimbursed — the receipts total, capped — and
    /// <paramref name="ibanMasked"/> is the payee IBAN snapshotted on the report, already masked:
    /// the raw IBAN never leaves the section.
    /// </summary>
    public EmailMessage ReportApproved(
        string userEmail, string userName, Guid reportId, decimal payable, string ibanMasked,
        string? culture = null)
        => Localized(culture, () => new EmailMessage(
            userEmail, userName,
            L("Expenses_Email_Approved_Subject"),
            Lf("Expenses_Email_Approved_Body",
                Encode(userName), Euros(payable), Encode(ibanMasked), AbsoluteUrl($"/Expenses/{reportId}")),
            "expense_approved", MessageCategory.System));

    /// <summary>Amount in the recipient's own number format — "1.234,56 €" in es, "1,234.56 €" in en.</summary>
    private static string Euros(decimal amount) =>
        amount.ToString("N2", CultureInfo.CurrentCulture) + " €";

    /// <summary>
    /// Webmail has no Humans origin, so a root-relative path in an email resolves against
    /// the reader's own host; callers pass the in-app path and the absolute link is built here.
    /// </summary>
    private string AbsoluteUrl(string path) =>
        $"{_settings.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

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

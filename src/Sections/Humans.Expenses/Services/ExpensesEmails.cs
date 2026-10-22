using System.Globalization;
using System.Net;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Humans.Expenses.Services;

/// <summary>Pure: no I/O, no persistence.</summary>
internal sealed class ExpensesEmails(
    IOptions<EmailSettings> settings,
    IStringLocalizer<ExpensesResource> localizer,
    ILogger<ExpensesEmails> logger)
{
    private readonly EmailSettings _settings = settings.Value;

    /// <summary><paramref name="ibanMasked"/> is already masked: the raw IBAN never leaves the section.</summary>
    public EmailMessage ReportApproved(
        string userEmail, string userName, Guid reportId, decimal payable, string ibanMasked,
        string? culture = null)
        => Localized(culture, () => new EmailMessage(
            userEmail, userName,
            L("Expenses_Email_Approved_Subject"),
            Lf("Expenses_Email_Approved_Body",
                Encode(userName), payable.ToEuro(), Encode(ibanMasked), AbsoluteUrl($"/Expenses/{reportId}")),
            "expense_approved", MessageCategory.System));

    /// <summary>Webmail has no Humans origin, so a root-relative link would resolve against the reader's host.</summary>
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

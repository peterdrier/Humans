using System.Globalization;
using System.Net;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Finance.Services;

/// <summary>Pure: no I/O, no persistence.</summary>
internal sealed class FinanceEmails(
    IStringLocalizer<FinanceResource> localizer,
    ILogger<FinanceEmails> logger)
{
    /// <summary><paramref name="ibanMasked"/> is already masked; the raw IBAN goes nowhere but the file and its row.</summary>
    public EmailMessage SepaPayoutGenerated(
        Guid userId, string userEmail, string userName, decimal amount, string ibanMasked, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            userEmail, userName, userId,
            L("Finance_Email_SepaPayoutGenerated_Subject"),
            Lf("Finance_Email_SepaPayoutGenerated_Body", Encode(userName), amount.ToEuro(), Encode(ibanMasked)),
            "sepa_payout_generated", MessageCategory.System));

    /// <summary><paramref name="announcedOn"/> is the file's generation day in the organisation's zone.</summary>
    public EmailMessage SepaPayoutWithdrawn(
        Guid userId, string userEmail, string userName, decimal amount, string ibanMasked, LocalDate announcedOn,
        string? culture = null)
        => Localized(culture, () => new EmailMessage(
            userEmail, userName, userId,
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

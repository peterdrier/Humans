using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Finance.Domain;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Finance.Services;

internal sealed class SepaPayoutEmails(
    IUserService users,
    IUserEmailService userEmails,
    IEmailService emailService,
    FinanceEmails emails,
    ILogger logger)
{
    /// <summary>
    /// After the save: an outbox row is a promise of money. Sent at generation, when the treasurer hands the file to
    /// the bank, not at booking. A failure here must not block the download of the saved file.
    /// </summary>
    public async Task SendPayoutEmailsAsync(IReadOnlyList<SepaPayoutTransfer> transfers)
    {
        var ct = CancellationToken.None;
        var userIds = transfers.Select(t => t.UserId).Distinct().ToList();
        IReadOnlyDictionary<Guid, UserInfo> infos;
        IReadOnlyDictionary<Guid, string> targets;
        try
        {
            infos = await users.GetUserInfosAsync(userIds, ct);
            targets = await userEmails.GetNotificationTargetEmailsAsync(userIds, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve recipients for saved SEPA payout {FileId}", transfers[0].FileId);
            return;
        }

        foreach (var t in transfers)
        {
            if (!infos.TryGetValue(t.UserId, out var member)
                || !targets.TryGetValue(t.UserId, out var recipient) || string.IsNullOrWhiteSpace(recipient))
            {
                logger.LogWarning(
                    "Skipping SEPA payout email for transfer {TransferId}: member {UserId} has no notification email",
                    t.Id, t.UserId);
                continue;
            }

            try
            {
                var language = member.PreferredLanguage;
                await emailService.SendAsync(emails.SepaPayoutGenerated(
                    member.Id,
                    recipient, member.BurnerName, t.Amount, t.IbanMasked,
                    language.IsSupportedCultureCode() ? language : CultureCatalog.DefaultCultureCode), ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to queue email for saved SEPA payout transfer {TransferId}", t.Id);
            }
        }
    }

    /// <summary>The member was told a transfer was coming, so they are told it was cancelled. After the save, with no
    /// request token.</summary>
    public async Task SendWithdrawnEmailAsync(SepaPayoutTransfer t, LocalDate announcedOn)
    {
        var ct = CancellationToken.None;
        try
        {
            var infos = await users.GetUserInfosAsync([t.UserId], ct);
            var targets = await userEmails.GetNotificationTargetEmailsAsync([t.UserId], ct);
            if (!infos.TryGetValue(t.UserId, out var member)
                || !targets.TryGetValue(t.UserId, out var recipient) || string.IsNullOrWhiteSpace(recipient))
            {
                logger.LogWarning(
                    "Skipping SEPA withdrawal email for transfer {TransferId}: member {UserId} has no notification email",
                    t.Id, t.UserId);
                return;
            }

            var language = member.PreferredLanguage;
            await emailService.SendAsync(emails.SepaPayoutWithdrawn(
                member.Id,
                recipient, member.BurnerName, t.Amount, t.IbanMasked, announcedOn,
                language.IsSupportedCultureCode() ? language : CultureCatalog.DefaultCultureCode), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to queue email for withdrawn SEPA payout transfer {TransferId}", t.Id);
        }
    }
}

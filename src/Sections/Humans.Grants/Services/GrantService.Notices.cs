using System.Globalization;
using Humans.Base.Extensions;
using Humans.Grants.Domain;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;

namespace Humans.Grants.Services;

/// <summary>
/// Telling the person: an in-app notice and an email, both in their language from the
/// section's resx. Failures never fail the write: the row and the audit entry are the record,
/// and a manager can resend.
/// </summary>
internal sealed partial class GrantService
{
    private static readonly System.Resources.ResourceManager NoticeResources = new(typeof(GrantsResource));

    /// <summary>The member's own profile, where the "My grants" card lives.</summary>
    internal const string ProfileUrl = "/Profile/Me";

    private Task AnnounceIssuedAsync(Grant grant, GrantProgram program, CancellationToken ct) =>
        AnnounceAsync(grant, program, GrantNoticeKind.Issued, NotificationSource.GrantReceived, ct);

    private Task AnnounceRevokedAsync(Grant grant, GrantProgram program, CancellationToken ct) =>
        AnnounceAsync(grant, program, GrantNoticeKind.Revoked, NotificationSource.GrantRevoked, ct);

    private async Task AnnounceAsync(
        Grant grant, GrantProgram program, GrantNoticeKind kind, NotificationSource source, CancellationToken ct)
    {
        // Each step is best-effort on its own: one channel's outage never suppresses the other.
        UserInfo? person = null;
        await BestEffortAsync("recipient lookup", kind, grant.Id,
            async () => person = await users.GetUserInfoAsync(grant.UserId, ct), ct);
        var language = person?.PreferredLanguage;
        var culture = language.IsSupportedCultureCode() ? language! : CultureCatalog.DefaultCultureCode;
        var expiresOn = grant.ExpiresOn ?? program.GrantsExpireOn;

        await BestEffortAsync("in-app notice", kind, grant.Id,
            () => NotifyAsync(person?.Id ?? grant.UserId, culture, kind, source, program, expiresOn, grant.RevokeReason, ct), ct);

        await BestEffortAsync("email", kind, grant.Id, async () =>
        {
            var addresses = await userEmails.GetNotificationTargetEmailsAsync([grant.UserId], ct);
            if (!addresses.TryGetValue(grant.UserId, out var address) || string.IsNullOrWhiteSpace(address))
            {
                logger.LogWarning("Skipping grant {Kind} email for {UserId} on grant {GrantId}: no effective email",
                    kind, grant.UserId, grant.Id);
                return;
            }
            await email.SendAsync(emailFactory.Notice(new GrantNoticeRequest(
                address, person?.BurnerName, kind, program.Name, program.Entitles, expiresOn, ProfileUrl,
                grant.RevokeReason, culture)), ct);
        }, ct);
    }

    private async Task BestEffortAsync(string step, GrantNoticeKind kind, Guid grantId, Func<Task> send, CancellationToken ct)
    {
        try
        {
            await send();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed the grant {Kind} {Step} for grant {GrantId}", kind, step, grantId);
        }
    }

    private async Task NotifyAsync(
        Guid recipientId, string cultureName, GrantNoticeKind kind, NotificationSource source, GrantProgram program,
        NodaTime.LocalDate? expiresOn, string? reason, CancellationToken ct)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        using var cultureScope = new CultureScope(culture.Name, logger);
        var titleKey = kind == GrantNoticeKind.Issued ? "Grants_Notice_Issued" : "Grants_Notice_Revoked";
        var title = $"{NoticeResources.GetString(titleKey, culture)}: {program.Name}";
        var body = kind == GrantNoticeKind.Issued
            ? string.Format(culture, NoticeResources.GetString("Grants_Notice_Issued_Body", culture)!, program.Entitles)
            : string.Format(culture, NoticeResources.GetString("Grants_Notice_Revoked_Body", culture)!, program.Entitles);
        if (kind == GrantNoticeKind.Issued && expiresOn is { } expires)
            body += " " + string.Format(culture, NoticeResources.GetString("Grants_Notice_ExpiresOn", culture)!, expires.ToDate());
        if (kind == GrantNoticeKind.Revoked && !string.IsNullOrEmpty(reason))
            body += " " + reason;
        await notifications.SendAsync(source, NotificationClass.Informational, NotificationPriority.Normal, title,
            [recipientId], body, actionUrl: ProfileUrl, cancellationToken: ct);
    }
}

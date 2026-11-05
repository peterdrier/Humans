using System.Globalization;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Base.Extensions;
using Humans.Grants.Domain;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;

namespace Humans.Grants.Services;

/// <summary>Failures never fail the write: the row and the audit entry are the record, and a manager can resend.</summary>
internal sealed class GrantNotices(
    IUserService users,
    INotificationEmitter notifications,
    IEmailService email,
    GrantsEmails emailFactory,
    ILogger<GrantService> logger) : IApplicationService
{
    private static readonly System.Resources.ResourceManager NoticeResources = new(typeof(GrantsResource));

    internal const string ProfileUrl = "/Profile/Me";

    internal Task AnnounceIssuedAsync(Grant grant, GrantProgram program, CancellationToken ct) =>
        AnnounceAsync(grant, program, GrantNoticeKind.Issued, GrantsNotificationSources.GrantReceived, ct);

    internal Task AnnounceRevokedAsync(Grant grant, GrantProgram program, CancellationToken ct) =>
        AnnounceAsync(grant, program, GrantNoticeKind.Revoked, GrantsNotificationSources.GrantRevoked, ct);

    private async Task AnnounceAsync(
        Grant grant, GrantProgram program, GrantNoticeKind kind, NotificationSourceDefinition source, CancellationToken ct)
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
            // Reuse a successful lookup; email still gets its independent attempt after a lookup failure.
            var recipient = person ?? await users.GetUserInfoAsync(grant.UserId, ct);
            if (recipient is null || string.IsNullOrWhiteSpace(recipient.Email))
            {
                logger.LogWarning("Skipping grant {Kind} email for {UserId} on grant {GrantId}: no effective email",
                    kind, grant.UserId, grant.Id);
                return;
            }
            var emailLanguage = recipient.PreferredLanguage;
            var emailCulture = emailLanguage.IsSupportedCultureCode() ? emailLanguage! : CultureCatalog.DefaultCultureCode;
            await email.SendAsync(emailFactory.Notice(new GrantNoticeRequest(
                recipient.Id, recipient.Email, recipient.BurnerName, kind, program.Name, program.Entitles, expiresOn, ProfileUrl,
                grant.RevokeReason, emailCulture)), ct);
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
        Guid recipientId, string cultureName, GrantNoticeKind kind, NotificationSourceDefinition source, GrantProgram program,
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

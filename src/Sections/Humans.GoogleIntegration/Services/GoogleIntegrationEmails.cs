using System.Globalization;
using System.Net;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// GoogleIntegration's own email templates: one method per template, each returning a
/// ready <see cref="EmailMessage"/> (content plus the routing policy this section
/// chooses — template name and opt-out category) for the single
/// <see cref="IEmailService.SendAsync"/> path. Copy lives in the section's own resx set
/// and is rendered in the recipient's culture inside a <see cref="CultureScope"/>
/// (memory/architecture/email-templates-live-in-sender.md, peterdrier/Humans#1651).
/// Pure — no I/O, no persistence.
/// </summary>
internal sealed class GoogleIntegrationEmails(
    IStringLocalizer<GoogleIntegrationResource> localizer,
    ILogger<GoogleIntegrationEmails> logger)
{
    /// <summary>
    /// Workspace account credentials to the member's recovery address. Always-send and
    /// time-sensitive: <see cref="TimeSensitiveTemplates.WorkspaceCredentials"/> is what
    /// makes the outbox drain it immediately, so the template name is load-bearing.
    /// </summary>
    public EmailMessage WorkspaceCredentials(string recoveryEmail, string userName, string workspaceEmail, string tempPassword, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            recoveryEmail, userName,
            L("GoogleIntegration_Email_WorkspaceCredentials_Subject"),
            Lf("GoogleIntegration_Email_WorkspaceCredentials_Body", Encode(userName), Encode(workspaceEmail), Encode(tempPassword)),
            TimeSensitiveTemplates.WorkspaceCredentials));

    public EmailMessage GoogleGroupRemovalLossOfAccess(string removedEmail, string userName, string groupName, string groupEmail, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            removedEmail, userName,
            Lf("GoogleIntegration_Email_GoogleGroupRemoval_LossOfAccess_Subject", groupEmail),
            Lf("GoogleIntegration_Email_GoogleGroupRemoval_LossOfAccess_Body",
                Encode(userName), Encode(groupName), Encode(groupEmail)),
            "google_group_removal_loss_of_access", MessageCategory.System));

    public EmailMessage GoogleDriveRemovalLossOfAccess(string removedEmail, string userName, string folderName, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            removedEmail, userName,
            Lf("GoogleIntegration_Email_GoogleDriveRemoval_LossOfAccess_Subject", folderName),
            Lf("GoogleIntegration_Email_GoogleDriveRemoval_LossOfAccess_Body",
                Encode(userName), Encode(folderName)),
            "google_drive_removal_loss_of_access", MessageCategory.System));

    public EmailMessage GoogleAccessRemovalSecondaryCleanup(string removedEmail, string userName, string currentGoogleEmail, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            removedEmail, userName,
            Lf("GoogleIntegration_Email_GoogleAccessRemoval_SecondaryCleanup_Subject", removedEmail),
            Lf("GoogleIntegration_Email_GoogleAccessRemoval_SecondaryCleanup_Body",
                Encode(userName), Encode(removedEmail), Encode(currentGoogleEmail)),
            "google_access_removal_secondary_cleanup", MessageCategory.System));

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

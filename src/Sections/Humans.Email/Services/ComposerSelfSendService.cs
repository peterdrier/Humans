using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Email.Services;

/// <summary>
/// Backs the shared <c>_EmailComposer</c> "Send to me" button: queues the composer's current
/// subject/Markdown body, sanitized and branded exactly like a real send, to the signed-in human's
/// own notification address. Never takes a recipient from the caller. Internal — its only consumer
/// is <c>EmailPreviewController</c>.
/// </summary>
internal sealed class ComposerSelfSendService(
    IUserEmailService userEmailService,
    IUserServiceRead userService,
    IEmailService emailService,
    IAuditLogService audit,
    IStringLocalizer<EmailResource> localizer,
    ILogger<ComposerSelfSendService> logger)
{
    public const string TemplateName = "composer_self_test";

    /// <summary>
    /// Queues one outbox row to <paramref name="userId"/> and audits it. Returns the address it was
    /// queued to, or null when the human has no notification address (nothing is queued).
    /// </summary>
    public async Task<string?> SendToSelfAsync(
        Guid userId, string? subject, string? markdownBody, CancellationToken ct = default)
    {
        var emails = await userEmailService.GetNotificationTargetEmailsAsync([userId], ct);
        if (!emails.TryGetValue(userId, out var email) || string.IsNullOrWhiteSpace(email))
            return null;

        var user = await userService.GetUserInfoAsync(userId, ct);
        var fullSubject = string.IsNullOrWhiteSpace(subject)
            ? localizer["Email_ComposerSelfTest_DefaultSubject"].Value
            : string.Format(CultureInfo.CurrentCulture, localizer["Email_ComposerSelfTest_Subject"].Value, subject.Trim());

        await emailService.SendAsync(new EmailMessage(
            email, user?.BurnerName, fullSubject, SanitizedMarkdownRenderer.Render(markdownBody),
            TemplateName, MessageCategory.System, UserId: userId), ct);

        await audit.LogAsync(
            AuditAction.EmailComposerSelfTestSent, "User", userId,
            "Sent an email composer test message to themselves", userId);

        logger.LogInformation("Email composer self-test queued for user {UserId}", userId);
        return email;
    }
}

using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Email.Services;

/// <summary>Never takes a recipient from the caller.</summary>
internal sealed class ComposerSelfSendService(
    IUserEmailService userEmailService,
    IUserService userService,
    IEmailService emailService,
    IAuditLogService audit,
    IStringLocalizer<EmailResource> localizer,
    ILogger<ComposerSelfSendService> logger)
{
    public const string TemplateName = "composer_self_test";

    /// <summary>Returns the address queued to, or null (nothing queued) when the member has no notification address.</summary>
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

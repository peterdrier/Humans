using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Email.Services;

/// <summary>Never takes a recipient from the caller.</summary>
internal sealed class ComposerSelfSendService(
    IUserService userService,
    IEmailService emailService,
    IAuditLogService audit,
    IStringLocalizer<EmailResource> localizer,
    ILogger<ComposerSelfSendService> logger)
{
    public const string TemplateName = "composer_self_test";

    /// <summary>Returns the queued address, or null when the account cannot receive a self-test.</summary>
    public async Task<string?> SendToSelfAsync(
        Guid userId, string? subject, string? markdownBody, CancellationToken ct = default)
    {
        var user = await userService.GetUserInfoAsync(userId, ct);
        if (user is null || user.IsTombstone)
        {
            logger.LogWarning("Refused composer self-test for unavailable user {UserId}", userId);
            return null;
        }
        var email = user.Email;
        if (string.IsNullOrWhiteSpace(email))
            return null;
        userId = user.Id;

        var fullSubject = string.IsNullOrWhiteSpace(subject)
            ? localizer["Email_ComposerSelfTest_DefaultSubject"].Value
            : localizer["Email_ComposerSelfTest_Subject", subject.Trim()];

        // Closing the composer must not interrupt sending after the account read.
        await emailService.SendAsync(new EmailMessage(
            email, user.BurnerName, userId, fullSubject, SanitizedMarkdownRenderer.Render(markdownBody),
            TemplateName, MessageCategory.System), CancellationToken.None);

        try
        {
            await audit.LogAsync(
                EmailAuditActions.EmailComposerSelfTestSent, AuditSubjectTypes.User, userId,
                "Sent an email composer test message to themselves", userId);
        }
        catch (Exception ex)
        {
            // A retry would duplicate the email request that already succeeded.
            logger.LogCritical(ex, "Email composer self-test audit failed after sending for user {UserId}", userId);
        }

        logger.LogInformation("Email composer self-test queued for user {UserId}", userId);
        return email;
    }
}

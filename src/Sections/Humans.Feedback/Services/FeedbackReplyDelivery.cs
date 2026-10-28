using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Base.Threading;
using Humans.Email.Contracts;
using Humans.Feedback.Data;
using Humans.Feedback.Domain;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;

namespace Humans.Feedback.Services;

/// <summary>
/// Delivers durable Feedback replies and retries pending handoffs. Request and recurring-job
/// delivery share the same lock, pending-state check and stable Email delivery id.
/// </summary>
internal sealed class FeedbackReplyDelivery(
    IFeedbackRepository repository,
    IUserService userService,
    IUserEmailService userEmailService,
    IEmailService emailService,
    FeedbackEmails emailMessages,
    INotificationEmitter notificationService,
    IAuditLogService auditLogService,
    ILogger<FeedbackReplyDelivery> logger) : IApplicationService, IRecurringJob
{
    private static readonly TrackedLock ReplyDeliveryLock = new("Feedback.ReplyDelivery");

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var pending = await repository.GetPendingReplyEmailsAsync(cancellationToken);
        foreach (var message in pending.OrderBy(m => m.CreatedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CompleteAsync(message, cancellationToken);
        }
    }

    public async Task CompleteAsync(FeedbackMessage message, CancellationToken ct)
    {
        try
        {
            using var delivery = await ReplyDeliveryLock.AcquireAsync(logger, ct);
            var report = await repository.GetByIdAsync(message.FeedbackReportId, ct);
            var current = report?.Messages.FirstOrDefault(m => m.Id == message.Id);
            if (current?.EmailEnqueuePending != true)
            {
                message.EmailEnqueuePending = false;
                return;
            }

            var queued = await SendAdminResponseEmailAsync(report!, current, ct);
            if (!await repository.CompleteReplyEmailAsync(message.Id, ct)) return;
            message.EmailEnqueuePending = false;
            delivery.Dispose();

            if (queued)
            {
                var description = $"Queued response email for feedback {report!.Id}, reply {message.Id}";
                if (current.SenderUserId.HasValue)
                    await auditLogService.LogAsync(AuditAction.FeedbackResponseSent, AuditEntityTypes.FeedbackReport,
                        report.Id, description, current.SenderUserId.Value);
                else
                    await auditLogService.LogAsync(AuditAction.FeedbackResponseSent, AuditEntityTypes.FeedbackReport,
                        report.Id, description, "API");
            }
            await DispatchAdminReplyNotificationAsync(report!, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Feedback reply {MessageId} email handoff failed; pending replies retry automatically", message.Id);
        }
    }

    private async Task<bool> SendAdminResponseEmailAsync(
        FeedbackReport report, FeedbackMessage message, CancellationToken ct)
    {
        var reporter = await userService.GetUserInfoAsync(report.UserId, ct);
        var emails = await userEmailService.GetNotificationTargetEmailsAsync(
            [report.UserId], ct);

        if (reporter is not null && !reporter.IsTombstone && emails.TryGetValue(report.UserId, out var recipientEmail) &&
            !string.IsNullOrWhiteSpace(recipientEmail))
        {
            await emailService.SendAsync(emailMessages.FeedbackResponse(
                reporter.Id, recipientEmail, reporter.BurnerName,
                report.Description, message.Content,
                reporter.PreferredLanguage.IsSupportedCultureCode()
                    ? reporter.PreferredLanguage : CultureCatalog.DefaultCultureCode) with
            { DeliveryId = message.Id }, ct);
            return true;
        }
        else
        {
            logger.LogWarning(
                "Skipping feedback response email for report {ReportId} because user {UserId} has no effective email",
                report.Id, report.UserId);
            return false;
        }
    }

    private async Task DispatchAdminReplyNotificationAsync(
        FeedbackReport report, CancellationToken ct)
    {
        try
        {
            var reporter = await userService.GetUserInfoAsync(report.UserId, ct);
            if (reporter is null || reporter.IsTombstone) return;
            var language = reporter.PreferredLanguage;
            var notification = emailMessages.FeedbackResponseNotification(
                language.IsSupportedCultureCode() ? language : CultureCatalog.DefaultCultureCode);

            // No action link: /Feedback/{id} is Admin-only now, so the reporter would land on a 403.
            // The response text itself reaches them in the FeedbackResponse email.
            await notificationService.SendAsync(
                NotificationSource.FeedbackResponse,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                notification.Title,
                [report.UserId],
                body: notification.Body,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch FeedbackResponse notification for report {ReportId}", report.Id);
        }
    }

}

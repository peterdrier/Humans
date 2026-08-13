using Hangfire;
using Humans.Application.Interfaces;
using Humans.Surveys.Contracts;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Daily job that sends the one-time 7-day reminder to survey invitees who haven't completed.
/// </summary>
/// <remarks>
/// Delegates entirely to <see cref="ISurveyReminderSender.SendDueRemindersAsync"/> — the job never
/// touches a section DbContext or any repository directly
/// (design-rules §2c: jobs call services). It reaches Surveys through the section's contracts leaf
/// because recurring jobs are still named by concrete type in Shell's roll-call and stay in Base
/// (design §15.6b).
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class SendSurveyReminderJob(
    ISurveyReminderSender surveyReminders,
    ILogger<SendSurveyReminderJob> logger) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var reminded = await surveyReminders.SendDueRemindersAsync(cancellationToken);
        logger.LogInformation("Survey reminder job sent {Count} reminder(s)", reminded);
    }
}

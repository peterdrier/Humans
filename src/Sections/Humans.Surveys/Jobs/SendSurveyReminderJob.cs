using Hangfire;
using Humans.Base.Interfaces;
using Humans.Surveys.Contracts;

namespace Humans.Surveys.Jobs;

/// <summary>Daily: the one-time 7-day reminder to invitees who haven't completed.</summary>
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

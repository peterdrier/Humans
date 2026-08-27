using Hangfire;
using Humans.Base.Interfaces;
using Humans.Surveys.Contracts;

namespace Humans.Surveys.Jobs;

/// <summary>
/// Daily job that sends the one-time 7-day reminder to survey invitees who haven't completed.
/// </summary>
/// <remarks>
/// Delegates entirely to <see cref="ISurveyReminderSender.SendDueRemindersAsync"/> — the job never
/// touches a section DbContext or any repository directly
/// (design-rules §2c: jobs call services). It sits under <c>Jobs/</c> because Shell
/// names the concrete type at registration and HUM0034 makes every other public type in a
/// section an error.
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

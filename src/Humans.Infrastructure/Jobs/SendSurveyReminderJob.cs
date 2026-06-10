using Hangfire;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Surveys;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Daily job that sends the one-time 7-day reminder to survey invitees who haven't completed.
/// </summary>
/// <remarks>
/// Delegates entirely to <see cref="ISurveyService.SendDueRemindersAsync"/> — the job never touches
/// <see cref="Humans.Infrastructure.Data.HumansDbContext"/> or any repository directly
/// (design-rules §2c: jobs call services).
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class SendSurveyReminderJob(
    ISurveyService surveyService,
    ILogger<SendSurveyReminderJob> logger) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var reminded = await surveyService.SendDueRemindersAsync(cancellationToken);
        logger.LogInformation("Survey reminder job sent {Count} reminder(s)", reminded);
    }
}

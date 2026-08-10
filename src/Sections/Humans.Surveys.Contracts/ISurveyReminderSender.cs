namespace Humans.Surveys.Contracts;

public interface ISurveyReminderSender
{
    /// <summary>
    /// Sends the one-time 7-day reminder to every invitee who has not completed, and returns
    /// how many were sent. Idempotent per invitation — an invitee already reminded is skipped.
    /// </summary>
    Task<int> SendDueRemindersAsync(CancellationToken ct = default);
}

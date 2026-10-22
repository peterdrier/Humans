namespace Humans.Surveys.Contracts;

public interface ISurveyReminderSender
{
    /// <summary>
    /// The one-time 7-day reminder to every invitee who has not completed. Returns how many were sent. An invitee
    /// already reminded is skipped.
    /// </summary>
    Task<int> SendDueRemindersAsync(CancellationToken ct = default);
}

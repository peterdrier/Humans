using Humans.Email.Contracts;

namespace Humans.Tasks.Services;

/// <summary>Built through the real send path's builder, so the gallery cannot drift from what members receive.</summary>
internal sealed class TasksEmailPreviews(TasksEmails emails) : IEmailPreviewContributor
{
    private const string SampleTask = "Set up the welfare tent";
    private const string SampleTeam = "Participant Wellness";

    public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);

        TaskAnswerEmail Sample(string? reason = null) => new(
            Guid.Empty, persona.Email, persona.Name, Guid.Empty, SampleTask, SampleTeam, reason, persona.Culture);

        return
        [
            new EmailPreviewSample("task-request-approved", "Task Request Approved", emails.RequestApproved(Sample())),
            new EmailPreviewSample("task-request-declined", "Task Request Declined (with reason)",
                emails.RequestDeclined(Sample("We already have enough people on the tent this time."))),
            new EmailPreviewSample("task-request-declined-no-reason", "Task Request Declined (no reason)",
                emails.RequestDeclined(Sample())),
        ];
    }
}

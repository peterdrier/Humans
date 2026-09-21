using Humans.Email.Contracts;
using Humans.Events.Contracts;

namespace Humans.Events.Services;

/// <summary>
/// Events' contribution to the template gallery at <c>/Email/EmailPreview</c>: one
/// sample per template <see cref="EventsEmails"/> can send — one per lifecycle status —
/// built through the same builder the real send path uses, so the gallery cannot drift
/// from what members receive (peterdrier/Humans#1651). Sample data only — no recipient is
/// real and nothing is sent.
/// </summary>
internal sealed class EventsEmailPreviews(EventsEmails emails) : IEmailPreviewContributor
{
    private const string SampleTitle = "Sunrise Yoga at the Temple";
    private const string SampleReason = "The listed times overlap with the closing ceremony.";
    private const string SampleActionUrl = "/Events/Mine";

    public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);
        var (culture, name, email) = (persona.Culture, persona.Name, persona.Email);

        return
        [
            new EmailPreviewSample("event-submitted", "Event Submitted",
                emails.EventLifecycle(Sample(EventStatus.Pending, name, culture), email)),
            new EmailPreviewSample("event-approved", "Event Approved",
                emails.EventLifecycle(Sample(EventStatus.Approved, name, culture), email)),
            new EmailPreviewSample("event-rejected", "Event Rejected",
                emails.EventLifecycle(Sample(EventStatus.Rejected, name, culture), email)),
            new EmailPreviewSample("event-resubmit-requested", "Event Changes Requested",
                emails.EventLifecycle(Sample(EventStatus.ResubmitRequested, name, culture), email)),
        ];
    }

    private static EventLifecycleNotification Sample(EventStatus status, string name, string culture) =>
        new(status, name, SampleTitle, SampleReason, SampleActionUrl, culture);
}

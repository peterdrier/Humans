using Humans.Email.Contracts;

namespace Humans.Feedback.Services;

/// <summary>
/// Feedback's contribution to the template gallery at <c>/Email/EmailPreview</c>: one
/// sample per template <see cref="FeedbackEmails"/> can send, built through the same
/// builder the real send path uses, so the gallery cannot drift from what members receive
/// (peterdrier/Humans#1651). Sample data only — no recipient is real and nothing is sent.
/// </summary>
internal sealed class FeedbackEmailPreviews(FeedbackEmails emails) : IEmailPreviewContributor
{
    private const string SampleDescription = "The shifts page shows the wrong timezone on my phone.";

    private const string SampleResponse =
        "Thanks for reporting this — we **fixed** the timezone handling and it will ship in the next release.";

    public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);
        var (culture, name, email) = (persona.Culture, persona.Name, persona.Email);

        return
        [
            new EmailPreviewSample("feedback-response", "Feedback Response",
                emails.FeedbackResponse(email, name, SampleDescription, SampleResponse, culture)),
        ];
    }
}

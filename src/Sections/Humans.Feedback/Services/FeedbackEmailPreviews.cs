using Humans.Email.Contracts;

namespace Humans.Feedback.Services;

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
                emails.FeedbackResponse(Guid.Empty, email, name, SampleDescription, SampleResponse, culture)),
        ];
    }
}

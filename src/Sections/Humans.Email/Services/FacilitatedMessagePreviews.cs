using Humans.Email.Contracts;

namespace Humans.Email.Services;

internal sealed class FacilitatedMessagePreviews(IEmailMessageFactory messages) : IEmailPreviewContributor
{
    private const string SampleText =
        "Hi! I'm organizing the next community event and would love your help. Let me know if you're interested!";

    public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);
        var (culture, name, email) = (persona.Culture, persona.Name, persona.Email);

        return
        [
            new EmailPreviewSample("facilitated-message", "Facilitated Message (with contact info)",
                messages.FacilitatedMessage(email, name, "Alex Firestone", SampleText, true, "alex@example.com", culture)),
            new EmailPreviewSample("facilitated-message-anon", "Facilitated Message (without contact info)",
                messages.FacilitatedMessage(email, name, "Alex Firestone", SampleText, false, null, culture)),
        ];
    }
}

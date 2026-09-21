using Humans.Email.Contracts;

namespace Humans.Email.Services;

/// <summary>
/// Email's own contribution to the template gallery at <c>/Email/EmailPreview</c>: the two
/// arms of the one template this section still owns, built through the same
/// <see cref="IEmailMessageFactory"/> the real send path uses, so the gallery cannot drift
/// from what members receive (peterdrier/Humans#1651). Sample data only — no recipient is
/// real and nothing is sent.
/// </summary>
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

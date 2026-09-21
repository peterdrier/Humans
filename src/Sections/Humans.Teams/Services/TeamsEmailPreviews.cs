using Humans.Email.Contracts;

namespace Humans.Teams.Services;

/// <summary>
/// Teams' contribution to the template gallery at <c>/Email/EmailPreview</c>: one
/// sample per template <see cref="TeamsEmails"/> can send, built through the same
/// builder the real send path uses, so the gallery cannot drift from what members receive
/// (peterdrier/Humans#1651). Sample data only — no recipient is real and nothing is sent.
/// </summary>
internal sealed class TeamsEmailPreviews(TeamsEmails emails) : IEmailPreviewContributor
{
    private static readonly (string Name, string? Url)[] SampleResources =
    [
        ("Art Collective Shared Drive", "https://drive.google.com/drive/folders/example"),
        ("art-collective@nobodies.team", "https://groups.google.com/g/art-collective"),
    ];

    public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);
        var (culture, name, email) = (persona.Culture, persona.Name, persona.Email);

        return
        [
            new EmailPreviewSample("added-to-team", "Added to Team",
                emails.AddedToTeam(email, name, "Art Collective", "art-collective", SampleResources, culture)),
        ];
    }
}

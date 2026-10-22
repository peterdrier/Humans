using Humans.Email.Contracts;

namespace Humans.Teams.Services;

/// <summary>Built through the real send path's builder, so the gallery can't drift from what members receive.</summary>
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
                emails.AddedToTeam(Guid.Empty, email, name, "Art Collective", "art-collective", SampleResources, culture)),
        ];
    }
}

using Humans.Email.Contracts;

namespace Humans.Issues.Services;

/// <summary>
/// Issues' contribution to the template gallery at <c>/Email/EmailPreview</c>: one
/// sample per template <see cref="IssuesEmails"/> can send, built through the same
/// builder the real send path uses, so the gallery cannot drift from what members receive
/// (peterdrier/Humans#1651). Sample data only — no recipient is real and nothing is sent.
/// </summary>
internal sealed class IssuesEmailPreviews(IssuesEmails emails) : IEmailPreviewContributor
{
    private const string SampleTitle = "Gate scanner rejects valid tickets";

    private const string SampleComment =
        "Thanks for the report — we **reproduced** it and a fix is on the way.";

    public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);
        var (culture, name, email) = (persona.Culture, persona.Name, persona.Email);

        return
        [
            new EmailPreviewSample("issue-comment", "Issue Comment",
                emails.IssueComment(email, name, SampleTitle, SampleComment, "/Issues/00000000-0000-0000-0000-000000000000", culture)),
        ];
    }
}

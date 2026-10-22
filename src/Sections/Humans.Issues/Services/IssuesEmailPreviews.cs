using Humans.Email.Contracts;

namespace Humans.Issues.Services;

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

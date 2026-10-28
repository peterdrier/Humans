using Humans.Email.Contracts;

namespace Humans.Campaigns.Services;

internal sealed class CampaignsEmailPreviews(CampaignsEmails emails) : IEmailPreviewContributor
{
    private const string SampleSubject = "Your ticket code for Elsewhere 2026, {{Name}}";

    private const string SampleBody =
        "Hi {{Name}},\n\nHere is your code: **{{Code}}**\n\nSee you there.";

    public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);

        return
        [
            new EmailPreviewSample("campaign-code", "Campaign Code",
                emails.CampaignCode(new CampaignCodeEmailRequest(
                    UserId: Guid.Empty,
                    CampaignId: Guid.Empty,
                    RecipientEmail: persona.Email,
                    RecipientName: persona.Name,
                    Subject: SampleSubject,
                    MarkdownBody: SampleBody,
                    Code: "NC-2026-ABCD",
                    ReplyTo: null))),
        ];
    }
}

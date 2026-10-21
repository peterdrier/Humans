using Humans.Email.Contracts;
using NodaTime;

namespace Humans.Grants.Services;

/// <summary>
/// Grants' contribution to the template gallery at <c>/Email/EmailPreview</c>: one sample per
/// notice kind, built through the same builder the real send path uses. Sample data only.
/// </summary>
internal sealed class GrantsEmailPreviews(GrantsEmails emails) : IEmailPreviewContributor
{
    public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);

        return
        [
            new EmailPreviewSample("grant-issued", "Grant — Issued",
                emails.Notice(new GrantNoticeRequest(
                    persona.Email, persona.Name, GrantNoticeKind.Issued, "Build crew 2027",
                    "a ticket at the build-crew price", new LocalDate(2027, 5, 1), GrantService.ProfileUrl,
                    Culture: persona.Culture))),
            new EmailPreviewSample("grant-revoked", "Grant — Revoked",
                emails.Notice(new GrantNoticeRequest(
                    persona.Email, persona.Name, GrantNoticeKind.Revoked, "Build crew 2027",
                    "a ticket at the build-crew price", null, GrantService.ProfileUrl,
                    "You are no longer on the build crew.", persona.Culture))),
        ];
    }
}

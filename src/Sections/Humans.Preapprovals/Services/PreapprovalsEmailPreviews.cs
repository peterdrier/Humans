using Humans.Email.Contracts;
using NodaTime;

namespace Humans.Preapprovals.Services;

/// <summary>Built through the same builder the real send path uses.</summary>
internal sealed class PreapprovalsEmailPreviews(PreapprovalsEmails emails) : IEmailPreviewContributor
{
    private static readonly Dictionary<PreapprovalNoticeKind, string?> Details = new()
    {
        [PreapprovalNoticeKind.Declined] = "The project is outside this year's theme.",
        [PreapprovalNoticeKind.ApplicationReceived] = "Dusty Nobody",
        [PreapprovalNoticeKind.AmountChanged] = "1,000.00 €",
        [PreapprovalNoticeKind.Revoked] = "The project was cancelled.",
    };

    public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);

        return
        [
            .. Enum.GetValues<PreapprovalNoticeKind>().Select(kind => new EmailPreviewSample(
                $"preapproval-{Kebab(kind)}",
                $"Pre-approval — {kind}",
                emails.Notice(new PreapprovalNoticeRequest(
                    Guid.Empty, persona.Email, persona.Name, kind, "2026 Creativity", "Fire Lotus", 1500m,
                    new LocalDate(2026, 11, 30), "/Preapprovals",
                    Details.GetValueOrDefault(kind), persona.Culture)))),
        ];
    }

    private static string Kebab(PreapprovalNoticeKind kind) =>
        string.Concat(kind.ToString().Select((c, i) =>
            char.IsUpper(c) && i > 0 ? $"-{char.ToLowerInvariant(c)}" : $"{char.ToLowerInvariant(c)}"));
}

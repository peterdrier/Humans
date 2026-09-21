using Humans.Email.Contracts;

namespace Humans.Workgroups.Services;

/// <summary>
/// Workgroups' contribution to the template gallery at <c>/Email/EmailPreview</c>: one
/// sample per notice kind <see cref="WorkgroupsEmails"/> can send, built through the
/// same builder the real send path uses, so the gallery cannot drift from what members
/// receive (peterdrier/Humans#1651). Sample data only — no recipient is real and nothing
/// is sent.
/// </summary>
internal sealed class WorkgroupsEmailPreviews(WorkgroupsEmails emails) : IEmailPreviewContributor
{
    private const string SampleName = "Health & Safety";
    private const string SampleSlug = "health-safety";

    /// <summary>The kinds that carry a <c>Detail</c> line, and the sample that fills it.</summary>
    private static readonly Dictionary<WorkgroupNoticeKind, string> Details = new()
    {
        [WorkgroupNoticeKind.Refused] = "The scope overlaps an existing working group.",
        [WorkgroupNoticeKind.Withdrawn] = "The proposers withdrew the application.",
        [WorkgroupNoticeKind.DormancyInquiry] = "90",
        [WorkgroupNoticeKind.Delivered] = "Incident response protocol v2",
        [WorkgroupNoticeKind.DispositionRecorded] = "Accepted — the Board adopted the protocol.",
    };

    public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);

        return
        [
            .. Enum.GetValues<WorkgroupNoticeKind>().Select(kind => new EmailPreviewSample(
                $"workgroup-notice-{Kebab(kind)}",
                $"Working Group Notice — {kind}",
                emails.WorkgroupNotice(new WorkgroupNoticeRequest(
                    persona.Email, persona.Name, kind, SampleName, SampleSlug,
                    Details.GetValueOrDefault(kind), persona.Culture)))),
            // RecipientName null/empty is the role-inbox case (e.g. the Board), which takes
            // the Greeting_Generic arm instead of the named greeting above.
            new EmailPreviewSample("workgroup-notice-role-inbox", "Workgroup Notice (role inbox, generic greeting)",
                emails.WorkgroupNotice(new WorkgroupNoticeRequest(
                    persona.Email, null, WorkgroupNoticeKind.Registered, SampleName, SampleSlug,
                    Culture: persona.Culture))),
        ];
    }

    private static string Kebab(WorkgroupNoticeKind kind) =>
        string.Concat(kind.ToString().Select((c, i) =>
            char.IsUpper(c) && i > 0 ? $"-{char.ToLowerInvariant(c)}" : $"{char.ToLowerInvariant(c)}"));
}

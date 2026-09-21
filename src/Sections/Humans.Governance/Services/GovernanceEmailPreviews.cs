using Humans.Email.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Governance.Services;

/// <summary>
/// Governance's contribution to the template gallery at <c>/Email/EmailPreview</c>: one
/// sample per template <see cref="GovernanceEmails"/> can send, built through the same
/// builder the real send path uses, so the gallery cannot drift from what members receive
/// (peterdrier/Humans#1651). Sample data only — no recipient is real and nothing is sent.
/// </summary>
internal sealed class GovernanceEmailPreviews(GovernanceEmails emails) : IEmailPreviewContributor
{
    private static readonly Guid SampleVoteId = new("11111111-1111-1111-1111-111111111111");

    private const string SampleVoteTitle = "Approve the 2026 annual budget";
    private const string SampleReason = "Incomplete profile information";

    private static readonly LocalDateTime SampleClosesAt = new(2026, 4, 1, 18, 0);

    public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);
        var (culture, name, email) = (persona.Culture, persona.Name, persona.Email);
        var voteUrl = $"/Governance/Votes/{SampleVoteId}";

        return
        [
            new EmailPreviewSample("application-approved", "Application Approved",
                emails.ApplicationApproved(email, name, MembershipTier.Colaborador, culture)),
            new EmailPreviewSample("application-rejected", "Application Rejected",
                emails.ApplicationRejected(email, name, MembershipTier.Asociado, SampleReason, culture)),
            new EmailPreviewSample("term-renewal-reminder", "Term Renewal Reminder",
                emails.TermRenewalReminder(email, name, "Colaborador", "April 1, 2026", culture)),

            // Official on the opened sample, indicative on the reminder, so the gallery shows
            // the "not counted" caveat in both of its states rather than only the empty one.
            new EmailPreviewSample("assembly-vote-opened", "Assembly Vote Opened",
                emails.AssemblyVoteOpened(email, name, SampleVoteTitle, SampleClosesAt, isOfficial: true, voteUrl, culture)),
            new EmailPreviewSample("assembly-vote-reminder", "Assembly Vote Reminder (indicative)",
                emails.AssemblyVoteReminder(email, name, SampleVoteTitle, SampleClosesAt, isOfficial: false, voteUrl, culture)),
            new EmailPreviewSample("assembly-vote-cancelled", "Assembly Vote Cancelled",
                emails.AssemblyVoteCancelled(email, name, SampleVoteTitle, "The motion was withdrawn by the Board", culture)),
        ];
    }
}

using Humans.Base.Interfaces;
using Humans.Users.ViewComponents;

namespace Humans.Users;

/// <summary>
/// Contributes Users' own view components to slots hosted by other sections
/// (nobodies-collective/Humans#1815): <see cref="ProfileCardViewComponent"/> on the Board
/// tier-vote and onboarding-review applicant pages, and <see cref="HumanSummaryViewComponent"/>
/// on the ticket-transfer admin detail. Discovered by <c>RegisterContributions</c>
/// (parameterless ctor).
/// </summary>
internal sealed class SectionUserParts : IUserPart
{
    public IEnumerable<UserPart> Parts() =>
    [
        new(UserPartSlots.BoardVoteApplicant, typeof(ProfileCardViewComponent)),
        new(UserPartSlots.OnboardingReviewApplicant, typeof(ProfileCardViewComponent)),
        new(UserPartSlots.TicketTransferParty, typeof(HumanSummaryViewComponent)),
    ];
}

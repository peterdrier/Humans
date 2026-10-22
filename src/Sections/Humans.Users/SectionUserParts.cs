using Humans.Base.Interfaces;
using Humans.Users.ViewComponents;

namespace Humans.Users;

/// <summary>
/// Users' view components in other sections' slots. Discovered by <c>RegisterContributions</c>,
/// which needs a parameterless ctor.
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

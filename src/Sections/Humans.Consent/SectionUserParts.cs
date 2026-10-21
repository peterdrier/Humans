using Humans.Base.Interfaces;
using Humans.Consent.ViewComponents;

namespace Humans.Consent;

/// <summary>
/// Contributes the Consent review card to the admin human-detail page and the rejection reason to
/// the member's own account-status wall. Discovered by <c>RegisterContributions</c>
/// (parameterless ctor).
/// </summary>
internal sealed class SectionUserParts : IUserPart
{
    public IEnumerable<UserPart> Parts() =>
    [
        new(UserPartSlots.AdminDetail, typeof(ConsentReviewCardViewComponent)),
        new(UserPartSlots.AccountStatus, typeof(RejectionReasonViewComponent)),
    ];
}

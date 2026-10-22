using Humans.Base.Interfaces;
using Humans.Consent.ViewComponents;

namespace Humans.Consent;

internal sealed class SectionUserParts : IUserPart
{
    public IEnumerable<UserPart> Parts() =>
    [
        new(UserPartSlots.AdminDetail, typeof(ConsentReviewCardViewComponent)),
        new(UserPartSlots.AccountStatus, typeof(RejectionReasonViewComponent)),
    ];
}

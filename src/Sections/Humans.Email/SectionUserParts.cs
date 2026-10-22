using Humans.Base.Interfaces;
using Humans.Email.ViewComponents;

namespace Humans.Email;

internal sealed class SectionUserParts : IUserPart
{
    public IEnumerable<UserPart> Parts() =>
    [
        new(UserPartSlots.AdminDetail, typeof(OutboxCardViewComponent), Weight: 200),
        new(UserPartSlots.AdminDetail, typeof(CommunicationPreferencesCardViewComponent), Weight: 210),
    ];
}

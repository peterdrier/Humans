using Humans.Base.Interfaces;
using Humans.Email.ViewComponents;

namespace Humans.Email;

/// <summary>
/// Contributes the "Email Outbox" card to the admin human-detail page, after the other
/// <c>AdminDetail</c> parts, and the "send a message from {team}" buttons to the profile page.
/// Discovered by <c>RegisterContributions</c> (parameterless ctor).
/// </summary>
internal sealed class SectionUserParts : IUserPart
{
    public IEnumerable<UserPart> Parts() =>
    [
        new(UserPartSlots.Profile, typeof(TeamMessageButtonsViewComponent), Weight: 200),
        new(UserPartSlots.AdminDetail, typeof(OutboxCardViewComponent), Weight: 200),
    ];
}

using Humans.Base.Interfaces;
using Humans.Email.ViewComponents;

namespace Humans.Email;

/// <summary>
/// Contributes the "Email Outbox" and read-only "Communication Preferences" cards to the admin
/// human-detail page, after the other <c>AdminDetail</c> parts.
/// Discovered by <c>RegisterContributions</c> (parameterless ctor).
/// </summary>
internal sealed class SectionUserParts : IUserPart
{
    public IEnumerable<UserPart> Parts() =>
    [
        new(UserPartSlots.AdminDetail, typeof(OutboxCardViewComponent), Weight: 200),
        new(UserPartSlots.AdminDetail, typeof(CommunicationPreferencesCardViewComponent), Weight: 210),
    ];
}

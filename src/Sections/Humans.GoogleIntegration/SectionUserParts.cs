using Humans.Base.Interfaces;
using Humans.GoogleIntegration.ViewComponents;

namespace Humans.GoogleIntegration;

/// <summary>
/// Contributes <see cref="WorkspaceIdentityCardViewComponent"/> (the Google Sync and
/// @nobodies.team cards) to the admin human-detail sidebar. Discovered by
/// <c>RegisterContributions</c> (parameterless ctor).
/// </summary>
internal sealed class SectionUserParts : IUserPart
{
    public IEnumerable<UserPart> Parts() =>
        [new(UserPartSlots.AdminDetailSidebar, typeof(WorkspaceIdentityCardViewComponent), Weight: 110)];
}

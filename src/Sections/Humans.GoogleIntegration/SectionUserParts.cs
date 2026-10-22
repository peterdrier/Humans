using Humans.Base.Interfaces;
using Humans.GoogleIntegration.ViewComponents;

namespace Humans.GoogleIntegration;

/// <summary><c>RegisterContributions</c> needs the parameterless ctor.</summary>
internal sealed class SectionUserParts : IUserPart
{
    public IEnumerable<UserPart> Parts() =>
        [new(UserPartSlots.AdminDetailSidebar, typeof(WorkspaceIdentityCardViewComponent), Weight: 110)];
}

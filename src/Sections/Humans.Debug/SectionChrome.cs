using Humans.Base.Interfaces;
using Humans.Debug.ViewComponents;

namespace Humans.Debug;

internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.AdminDashboard, typeof(UserSetMembershipCardViewComponent), Weight: 5)];
}

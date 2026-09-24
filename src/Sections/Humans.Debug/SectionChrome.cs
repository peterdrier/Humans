using Humans.Base.Interfaces;
using Humans.Debug.ViewComponents;

namespace Humans.Debug;

/// <summary>Layout chrome contribution — the user set-membership Venn/UpSet card on the admin dashboard.</summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.AdminDashboard, typeof(UserSetMembershipCardViewComponent), Weight: 5)];
}

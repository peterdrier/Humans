using Humans.Base.Interfaces;

namespace Humans.Debug;

/// <summary>Layout chrome contribution — the user set-membership Venn/UpSet card on the admin dashboard.</summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.AdminDashboard, "UserSetMembershipCard", Weight: 5)];
}

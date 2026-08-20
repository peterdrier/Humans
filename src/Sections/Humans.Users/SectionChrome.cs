using Humans.Base.Interfaces;

namespace Humans.Users;

/// <summary>Layout chrome contribution — the preferred-language card on the admin dashboard.</summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.AdminDashboard, "PreferredLanguageCard", Weight: 40)];
}

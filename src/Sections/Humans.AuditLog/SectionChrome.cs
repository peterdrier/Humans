using Humans.Base.Interfaces;

namespace Humans.AuditLog;

/// <summary>Layout chrome contribution — the recent-activity card on the admin dashboard.</summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.AdminDashboard, "AdminActivityCard", Weight: 20)];
}

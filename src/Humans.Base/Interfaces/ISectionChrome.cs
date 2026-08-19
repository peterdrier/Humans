namespace Humans.Base.Interfaces;

/// <summary>Well-known chrome slot names. A slot with nothing in it renders nothing.</summary>
public static class ChromeSlots
{
    /// <summary>Right of the member top nav, beside the language and login partials.</summary>
    public const string HeaderRight = "header-right";

    /// <summary>Between the header and page content, on member and admin layouts.</summary>
    public const string AboveContent = "above-content";

    /// <summary>The member dashboard's contributed section area.</summary>
    public const string MemberDashboard = "member-dashboard";

    /// <summary>The profileless-account (guest) page's contributed section area.</summary>
    public const string GuestPage = "guest-page";
}

/// <summary>
/// A section view component rendered into a named chrome slot. Shell renders whatever the
/// active sections contributed, so a section that is off takes its chrome with it.
/// </summary>
public sealed record ChromeComponent(string Slot, string ComponentName, int Weight = 0);

/// <summary>The layout-chrome components a section contributes.</summary>
public interface ISectionChrome : ISectionContribution
{
    IEnumerable<ChromeComponent> Components();
}

/// <summary>
/// The dashboard components a section contributes. Separate from <see cref="ISectionChrome"/>
/// so layout chrome and dashboard content stay separately owned files.
/// </summary>
public interface ISectionMemberDashboard : ISectionContribution
{
    IEnumerable<ChromeComponent> Components();
}

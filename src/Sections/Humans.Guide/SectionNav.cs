using Humans.Base.Interfaces;

namespace Humans.Guide;

/// <summary>
/// No <c>Visible</c> predicate: the guide serves members and anonymous readers alike, and the per-block filter decides
/// what each sees. The label is a SharedResource key with no entry, so it renders as itself: Guide has no resource set.
/// </summary>
internal sealed class SectionNav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
    [
        new(
            "Guide",
            Controller: "Guide",
            Action: "Index",
            Weight: 1010)
    ];
}

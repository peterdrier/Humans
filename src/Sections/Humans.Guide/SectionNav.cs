using Humans.Base.Interfaces;

namespace Humans.Guide;

/// <summary>
/// Member top-nav contribution — the Guide's only reachable entry point for a signed-out
/// reader. The section serves anonymous readers deliberately (<c>GET /Guide</c> and
/// <c>GET /Guide/{name}</c> are <c>[AllowAnonymous]</c>, and the filter's anonymous branch
/// shows Volunteer blocks), but until this link the app's only route in was the signed-in
/// user menu, so that branch was unreachable in practice
/// (peterdrier/Humans#1655, N5 — Peter's call).
/// </summary>
/// <remarks>
/// Weight sits just past <c>Humans.Tour</c>'s 1000 so the two newcomer links render together.
/// Unlike Tour this one carries no <c>Visible</c> predicate: the guide is for members and
/// visitors alike, and the per-block role filter already decides what each reader sees.
/// The label is a SharedResource key that has no entry, which renders it as itself — the same
/// way Tour's literal label works, and why this adds no resource key to a section that
/// deliberately has no resource set (Docs/health.md §6).
/// </remarks>
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

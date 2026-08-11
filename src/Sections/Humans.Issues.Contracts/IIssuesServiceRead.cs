using Humans.Application.Interfaces;

namespace Humans.Issues.Contracts;

/// <summary>
/// Cross-section read surface for the Issues section — one method, because one thing
/// outside the section reads an issue: Shell's <c>NavBadgesViewComponent</c> renders the
/// per-viewer actionable count in the nav bar. See
/// <c>memory/architecture/section-read-write-split.md</c>.
/// </summary>
/// <remarks>
/// The section's own controllers take the internal <c>IIssuesService</c>, so the triage
/// surface — the list projection, the thread, submit, comment, status/assignee/section/
/// GitHub mutations — is not public at all (design §15 step 5).
/// </remarks>
public interface IIssuesServiceRead : IApplicationService
{
    /// <summary>
    /// Count of Open + Triage issues whose section maps to a role the viewer holds, plus
    /// their own non-terminal issues. Admins get the global non-terminal count.
    /// </summary>
    Task<int> GetActionableCountForViewerAsync(
        Guid viewerUserId, IReadOnlyList<string> viewerRoles, bool viewerIsAdmin,
        CancellationToken ct = default);
}

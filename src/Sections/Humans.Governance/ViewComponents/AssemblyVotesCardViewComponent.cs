using System.Security.Claims;
using Humans.Governance.Domain;
using Humans.Governance.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Governance.ViewComponents;

/// <summary>
/// The member dashboard's assembly-votes card. Renders only while at least one vote is open,
/// for every logged-in member — a member who is not on the roster still sees that the
/// association is deciding something (Docs/features/assembly-votes.md, decision 8).
/// </summary>
internal sealed class AssemblyVotesCardViewComponent(IAssemblyVoteService votes) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return Content(string.Empty);
        }

        var all = await votes.GetVotesForMemberAsync(userId, HttpContext.RequestAborted);
        return all.Any(v => v.Status == AssemblyVoteStatus.Open) ? View() : Content(string.Empty);
    }
}

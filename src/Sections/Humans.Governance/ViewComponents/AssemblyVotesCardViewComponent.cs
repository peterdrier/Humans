using System.Security.Claims;
using Humans.Governance.Domain;
using Humans.Governance.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Governance.ViewComponents;

/// <summary>Renders only while a vote is open, for every member — on the roster or not.</summary>
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

using System.Security.Claims;
using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Governance;

internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Governance", [
            new("Voting", "GovernanceBoardVoting", "BoardVoting", null, null, "fa-solid fa-check-to-slot", PolicyNames.BoardOrAdmin,
                 PillCount: PillCounts.VotingQueueAsync),
            new("Applications", "GovernanceApplications", "Admin", null, null, "fa-solid fa-file-signature", PolicyNames.BoardOrAdmin),
            new("Assembly Votes", "GovernanceVotesAdmin", "Index", null, null, "fa-solid fa-landmark-flag", PolicyNames.BoardOrAdmin),
            // Temporary: term-expiry repair for rows approved under the pre-September-2026 rule.
            new("Term expiry (temp)", "GovernanceApplications", "AdminTermExpiry", null, null, "fa-solid fa-calendar-check", PolicyNames.AdminOnly)
        ])
    ];
}

internal static class PillCounts
{
    public static async ValueTask<int?> VotingQueueAsync(IServiceProvider sp)
    {
        var http = sp.GetRequiredService<IHttpContextAccessor>();
        var idClaim = http.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier);
        if (idClaim is null || !Guid.TryParse(idClaim.Value, out var userId))
            return null;
        var applications = sp.GetRequiredService<IGovernanceService>();
        var count = await applications.GetUnvotedApplicationCountAsync(userId);
        return count > 0 ? count : null;
    }
}

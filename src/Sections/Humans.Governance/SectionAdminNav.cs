using System.Security.Claims;
using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Governance;

/// <summary>Governance's admin sidebar contribution — the "Governance" group.</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Governance", [
            new("Voting", "GovernanceBoardVoting", "BoardVoting", null, null, "fa-solid fa-check-to-slot", PolicyNames.BoardOrAdmin,
                 PillCount: PillCounts.VotingQueue),
            new("Applications", "GovernanceApplications", "Admin", null, null, "fa-solid fa-file-signature", PolicyNames.BoardOrAdmin)
        ], Weight: 70)
    ];
}

internal static class PillCounts
{
    public static async ValueTask<int?> VotingQueue(IServiceProvider sp)
    {
        var http = sp.GetRequiredService<IHttpContextAccessor>();
        var idClaim = http.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier);
        if (idClaim is null || !Guid.TryParse(idClaim.Value, out var userId))
            return null;
        var applications = sp.GetRequiredService<IApplicationServiceRead>();
        var count = await applications.GetUnvotedApplicationCountAsync(userId);
        return count > 0 ? count : null;
    }
}

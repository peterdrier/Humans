using Humans.Auth.Contracts;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using NodaTime;

namespace Humans.Governance.Services;

/// <summary>
/// Who is in each body before account filtering: an approved tier in term, or an active Board seat. Reads only this
/// section's repository and Auth, so the Google sources can use it without looping back through Google's own sync.
/// </summary>
internal sealed class GovernanceBodyRosters(
    IApplicationRepository applications,
    ISystemRoleService roles,
    IClock clock) : IApplicationService
{
    public async Task<IReadOnlyDictionary<GovernanceBody, IReadOnlyList<Guid>>> GetAsync(CancellationToken ct = default)
    {
        var today = clock.GetCurrentInstant().InUtc().Date;
        return new Dictionary<GovernanceBody, IReadOnlyList<Guid>>
        {
            [GovernanceBody.Asociados] = await applications.GetActiveApprovedTierUserIdsAsync(MembershipTier.Asociado, today, ct),
            [GovernanceBody.Colaboradores] = await applications.GetActiveApprovedTierUserIdsAsync(MembershipTier.Colaborador, today, ct),
            [GovernanceBody.Board] = await roles.GetActiveUserIdsInRoleAsync(RoleNames.Board, ct),
        };
    }
}

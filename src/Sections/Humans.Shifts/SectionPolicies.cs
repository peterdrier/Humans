using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Shifts.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Shifts;

/// <summary>
/// Shifts' authorization policies, at the project root by convention. Discovered by Shell
/// alongside <see cref="Section"/> — nothing names it.
/// </summary>
internal sealed class SectionPolicies : ISectionPolicies
{
    public void AddPolicies(AuthorizationOptions options)
    {
        // Intentionally identical to ShiftDepartmentManager today; kept separate for future divergence.
        options.AddPolicy(PolicyNames.ShiftDashboardAccess, policy =>
            policy.RequireRole(RoleNames.Admin, RoleNames.NoInfoAdmin, RoleNames.VolunteerCoordinator));

        options.AddPolicy(PolicyNames.VolunteerTrackingWrite, policy =>
            policy.RequireRole(RoleNames.Admin, RoleNames.VolunteerCoordinator));

        // Role-OR-team-coord disjunction encoded in IsAnyTeamManagerOrCoordinatorHandler so the policy is one requirement (policy requirements AND).
        options.AddPolicy(PolicyNames.ShiftDepartmentManager, policy =>
            policy.AddRequirements(new IsAnyTeamManagerOrCoordinatorRequirement()));

        options.AddPolicy(PolicyNames.PrivilegedSignupApprover, policy =>
            policy.RequireRole(RoleNames.Admin, RoleNames.NoInfoAdmin));

        options.AddPolicy(PolicyNames.VolunteerManager, policy =>
            policy.RequireRole(RoleNames.Admin, RoleNames.VolunteerCoordinator));

        options.AddPolicy(PolicyNames.MedicalDataViewer, policy =>
            policy.RequireRole(RoleNames.Admin, RoleNames.NoInfoAdmin));
    }
}

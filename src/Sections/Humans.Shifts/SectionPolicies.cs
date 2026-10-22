using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Shifts.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Shifts;

internal sealed class SectionPolicies : ISectionPolicies
{
    public void AddPolicies(AuthorizationOptions options)
    {
        // Three-role list; ShiftDepartmentManager below additionally admits any team coordinator.
        options.AddPolicy(PolicyNames.ShiftDashboardAccess, policy =>
            policy.RequireRole(RoleNames.Admin, RoleNames.NoInfoAdmin, RoleNames.VolunteerCoordinator));

        options.AddPolicy(PolicyNames.VolunteerTrackingWrite, policy =>
            policy.RequireRole(RoleNames.Admin, RoleNames.VolunteerCoordinator));

        // The role-or-team-coordinator disjunction lives in one handler because policy requirements AND.
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

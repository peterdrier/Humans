using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Tickets;

/// <summary>
/// Tickets' authorization policies, at the project root by convention. Discovered by Shell
/// alongside <see cref="Section"/> — nothing names it.
/// </summary>
/// <remarks>
/// <c>ScannerAccess</c> and <c>GateAdmit</c> stay in Shell: both also admit the shared
/// gate-terminal system account (a Gate-domain identity), so they are composites spanning
/// two sections.
/// </remarks>
internal sealed class SectionPolicies : ISectionPolicies
{
    public void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.TicketAdminBoardOrAdmin, policy =>
            policy.RequireRole(RoleNames.TicketAdmin, RoleNames.Admin, RoleNames.Board));

        options.AddPolicy(PolicyNames.TicketAdminOrAdmin, policy =>
            policy.RequireRole(RoleNames.TicketAdmin, RoleNames.Admin));
    }
}

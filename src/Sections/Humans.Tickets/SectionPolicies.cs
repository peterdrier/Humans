using Humans.Tickets.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Tickets;

/// <summary>
/// <c>ScannerAccess</c> and <c>GateAdmit</c> stay in Shell: both also admit the gate-terminal account, so they span
/// two sections.
/// </summary>
internal sealed class SectionPolicies : ISectionPolicies
{
    public void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.TicketAdminBoardOrAdmin, policy =>
            policy.RequireRole(TicketsRoles.TicketsAdmin, RoleNames.Admin, RoleNames.Board));

        options.AddPolicy(PolicyNames.TicketAdminOrAdmin, policy =>
            policy.RequireRole(TicketsRoles.TicketsAdmin, RoleNames.Admin));
    }
}

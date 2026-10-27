using Humans.Auth.Contracts;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Camps.Contracts;
using Humans.Finance.Contracts;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;

namespace Humans.Auth.Tests.Infrastructure;

/// <summary>A fixture catalog: the two system roles plus a few section roles, two of them renamed.</summary>
internal static class TestRoles
{
    public const string FormerUsersAdmin = "HumanAdmin";
    public const string FormerCampsAdmin = "CampAdmin";

    public static RoleCatalog Catalog() => new(new RoleDefinition[]
    {
        new(RoleNames.Admin, "Admin", "", ""),
        new(RoleNames.Board, "Board", "", ""),
        new(UsersRoles.UsersAdmin, "Users Admin", "", "", FormerNames: [FormerUsersAdmin]),
        new(TeamsRoles.TeamsAdmin, "Teams Admin", "", ""),
        new(CampsRoles.CampsAdmin, "Camps Admin", "", "", FormerNames: [FormerCampsAdmin]),
        new(TicketsRoles.TicketsAdmin, "Tickets Admin", "", ""),
        new(FinanceRoles.FinanceAdmin, "Finance Admin", "", ""),
    });
}

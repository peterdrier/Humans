using Humans.Auth.Contracts;
using Humans.Base.Interfaces;
using Humans.Guide.Services;

namespace Humans.Guide.Tests;

/// <summary>
/// A fixture catalog with the titles the guide corpus uses. The real catalog is composed from every section at
/// startup; <c>Humans.Web.Tests</c> checks the corpus against that one.
/// </summary>
internal static class GuideTestRoles
{
    public const string Admin = "Admin";
    public const string Board = "Board";
    public const string TeamsAdmin = "TeamsAdmin";
    public const string CampsAdmin = "CampsAdmin";
    public const string TicketsAdmin = "TicketsAdmin";
    public const string ShiftsAdmin = "ShiftsAdmin";
    public const string ShiftsNoInfo = "ShiftsNoInfo";
    public const string UsersAdmin = "UsersAdmin";
    public const string FinanceAdmin = "FinanceAdmin";
    public const string EventsAdmin = "EventsAdmin";
    public const string StoreAdmin = "StoreAdmin";
    public const string ConsentCoordinator = "ConsentCoordinator";

    public static readonly RoleCatalog Catalog = new(new RoleDefinition[]
    {
        new(Admin, "Admin", "", ""),
        new(Board, "Board", "", ""),
        new(TeamsAdmin, "Teams Admin", "", ""),
        new(CampsAdmin, "Camps Admin", "", ""),
        new(TicketsAdmin, "Tickets Admin", "", ""),
        new(ShiftsAdmin, "Shifts Admin", "", ""),
        new(ShiftsNoInfo, "Shifts NoInfo", "", ""),
        new(UsersAdmin, "Users Admin", "", ""),
        new(FinanceAdmin, "Finance Admin", "", ""),
        new(EventsAdmin, "Events Admin", "", ""),
        new(StoreAdmin, "Store Admin", "", ""),
        new(ConsentCoordinator, "Consent Coordinator", "", ""),
    });

    public static readonly GuideRolePrivilegeMap Map = new(Catalog);

    public static readonly GuideSegmenter Segmenter = new(Map);
}

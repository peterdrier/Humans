using System.Security.Claims;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Services.Store.Dtos;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Authorization;

/// <summary>
/// Covers the TeamsAdmin branch of <see cref="StoreOrderAuthorizationHandler"/>:
/// read any order, manage (edit/delete) team orders only, camp orders view-only.
/// </summary>
public class StoreOrderAuthorizationHandlerTests
{
    private readonly ICampServiceRead _campService = Substitute.For<ICampServiceRead>();
    private readonly ITeamServiceRead _teamService = Substitute.For<ITeamServiceRead>();
    private readonly StoreOrderAuthorizationHandler _handler;

    public StoreOrderAuthorizationHandlerTests()
    {
        _handler = new StoreOrderAuthorizationHandler(_campService, _teamService);
    }

    [HumansFact]
    public Task TeamsAdmin_can_view_camp_order() =>
        AssertOutcome(RoleNames.TeamsAdmin, MakeOrder(team: false), StoreOrderOperationRequirement.View, expectAllowed: true);

    [HumansFact]
    public Task TeamsAdmin_cannot_edit_camp_order() =>
        AssertOutcome(RoleNames.TeamsAdmin, MakeOrder(team: false), StoreOrderOperationRequirement.AddLine, expectAllowed: false);

    [HumansFact]
    public Task TeamsAdmin_can_edit_team_order() =>
        AssertOutcome(RoleNames.TeamsAdmin, MakeOrder(team: true), StoreOrderOperationRequirement.AddLine, expectAllowed: true);

    [HumansFact]
    public Task TeamsAdmin_can_delete_team_order() =>
        AssertOutcome(RoleNames.TeamsAdmin, MakeOrder(team: true), StoreOrderOperationRequirement.Delete, expectAllowed: true);

    [HumansFact]
    public Task TeamsAdmin_cannot_pay_team_order() =>
        AssertOutcome(RoleNames.TeamsAdmin, MakeOrder(team: true), StoreOrderOperationRequirement.Pay, expectAllowed: false);

    [HumansFact]
    public Task TeamsAdmin_cannot_edit_issued_team_order() =>
        AssertOutcome(RoleNames.TeamsAdmin, MakeOrder(team: true, StoreOrderState.InvoiceIssued), StoreOrderOperationRequirement.AddLine, expectAllowed: false);

    private async Task AssertOutcome(
        string role,
        OrderDto order,
        StoreOrderOperationRequirement requirement,
        bool expectAllowed)
    {
        var context = new AuthorizationHandlerContext([requirement], Principal(role), order);

        await _handler.HandleAsync(context);

        Assert.Equal(expectAllowed, context.HasSucceeded);
    }

    private static ClaimsPrincipal Principal(string role) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, role),
            ],
            authenticationType: "test"));

    private static OrderDto MakeOrder(bool team, StoreOrderState state = StoreOrderState.Open) =>
        new(
            Id: Guid.NewGuid(),
            CampSeasonId: team ? null : Guid.NewGuid(),
            TeamId: team ? Guid.NewGuid() : null,
            CounterpartyType: team ? StoreOrderCounterpartyType.Team : StoreOrderCounterpartyType.Camp,
            CounterpartyDisplayName: "x",
            Year: 2026,
            Label: null,
            State: state,
            CounterpartyName: null,
            CounterpartyVatId: null,
            CounterpartyAddress: null,
            CounterpartyCountryCode: null,
            CounterpartyEmail: null,
            IssuedInvoiceId: null,
            Lines: [],
            LinesSubtotalEur: 0m,
            VatTotalEur: 0m,
            DepositTotalEur: 0m,
            PaymentsTotalEur: 0m,
            BalanceEur: 0m);
}

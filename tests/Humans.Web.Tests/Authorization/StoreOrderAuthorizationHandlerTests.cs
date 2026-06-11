using System.Security.Claims;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Services.Store.Dtos;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Authorization;

/// <summary>
/// Covers the TeamsAdmin branch of <see cref="StoreOrderAuthorizationHandler"/>
/// (read any order, manage team orders only, camp orders view-only) and the
/// product-deadline gate on line edits (Store admins exempt, everyone else denied
/// past <c>OrderableUntil</c>).
/// </summary>
public class StoreOrderAuthorizationHandlerTests
{
    private static readonly LocalDate PastDeadline = new(2026, 1, 1);
    private static readonly LocalDate FutureDeadline = new(2026, 12, 31);

    private readonly ICampServiceRead _campService = Substitute.For<ICampServiceRead>();
    private readonly ITeamServiceRead _teamService = Substitute.For<ITeamServiceRead>();
    private readonly IShiftManagementService _shifts = Substitute.For<IShiftManagementService>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 14, 12, 0));
    private readonly StoreOrderAuthorizationHandler _handler;

    public StoreOrderAuthorizationHandlerTests()
    {
        _shifts.GetActiveAsync().Returns(new EventSettings
        {
            Year = 2026,
            TimeZoneId = "Europe/Madrid"
        });
        _handler = new StoreOrderAuthorizationHandler(_campService, _teamService, _shifts, _clock);
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

    [HumansFact]
    public Task Admin_can_add_line_past_deadline() =>
        AssertOutcome(RoleNames.Admin, new StoreOrderLineContext(MakeOrder(team: false), PastDeadline), StoreOrderOperationRequirement.AddLine, expectAllowed: true);

    [HumansFact]
    public Task Admin_can_remove_line_past_deadline() =>
        AssertOutcome(RoleNames.Admin, new StoreOrderLineContext(MakeOrder(team: false), PastDeadline), StoreOrderOperationRequirement.RemoveLine, expectAllowed: true);

    [HumansFact]
    public Task TeamsAdmin_cannot_add_line_past_deadline_on_team_order() =>
        AssertOutcome(RoleNames.TeamsAdmin, new StoreOrderLineContext(MakeOrder(team: true), PastDeadline), StoreOrderOperationRequirement.AddLine, expectAllowed: false);

    [HumansFact]
    public Task TeamsAdmin_cannot_remove_line_past_deadline_on_team_order() =>
        AssertOutcome(RoleNames.TeamsAdmin, new StoreOrderLineContext(MakeOrder(team: true), PastDeadline), StoreOrderOperationRequirement.RemoveLine, expectAllowed: false);

    [HumansFact]
    public Task TeamsAdmin_can_add_line_before_deadline_on_team_order() =>
        AssertOutcome(RoleNames.TeamsAdmin, new StoreOrderLineContext(MakeOrder(team: true), FutureDeadline), StoreOrderOperationRequirement.AddLine, expectAllowed: true);

    [HumansFact]
    public Task CampLead_cannot_add_line_past_deadline_on_camp_order() =>
        AssertLeadOutcome(PastDeadline, expectAllowed: false);

    [HumansFact]
    public Task CampLead_can_add_line_before_deadline_on_camp_order() =>
        AssertLeadOutcome(FutureDeadline, expectAllowed: true);

    private async Task AssertOutcome(
        string role,
        object resource,
        StoreOrderOperationRequirement requirement,
        bool expectAllowed)
    {
        var context = new AuthorizationHandlerContext([requirement], Principal(role), resource);

        await _handler.HandleAsync(context);

        Assert.Equal(expectAllowed, context.HasSucceeded);
    }

    private async Task AssertLeadOutcome(LocalDate deadline, bool expectAllowed)
    {
        var userId = Guid.NewGuid();
        var campId = Guid.NewGuid();
        var order = MakeOrder(team: false);
        var seasonId = order.CampSeasonId!.Value;
        var camp = MakeCampInfo(campId, seasonId, userId);
        _campService.GetCampSeasonByIdAsync(seasonId, Arg.Any<CancellationToken>())
            .Returns(camp.Seasons[0]);
        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([camp]);

        var context = new AuthorizationHandlerContext(
            [StoreOrderOperationRequirement.AddLine],
            Principal(role: null, userId),
            new StoreOrderLineContext(order, deadline));

        await _handler.HandleAsync(context);

        Assert.Equal(expectAllowed, context.HasSucceeded);
    }

    private static ClaimsPrincipal Principal(string? role, Guid? userId = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString())
        };
        if (role is not null)
            claims.Add(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
    }

    private static CampInfo MakeCampInfo(Guid campId, Guid seasonId, Guid leadUserId) =>
        new(
            campId,
            Slug: "camp-alpha",
            ContactEmail: string.Empty,
            ContactPhone: string.Empty,
            IsSwissCamp: false,
            TimesAtNowhere: 0,
            Seasons:
            [
                new CampSeasonInfo(
                    seasonId,
                    campId,
                    "camp-alpha",
                    2026,
                    null,
                    "Camp Alpha",
                    string.Empty,
                    string.Empty,
                    [],
                    CampSeasonStatus.Active,
                    YesNoMaybe.Yes,
                    YesNoMaybe.No,
                    AdultPlayspacePolicy.No,
                    MemberCount: 0,
                    SoundZone: null,
                    SpaceRequirement: null,
                    ElectricalGrid: null,
                    EeSlotCount: 0,
                    EeGrantedCount: null,
                    JoinedMemberCount: null)
                {
                    LeadUserIds = [leadUserId]
                }
            ]);

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
            Payments: [],
            LinesSubtotalEur: 0m,
            VatTotalEur: 0m,
            DepositTotalEur: 0m,
            PaymentsTotalEur: 0m,
            BalanceEur: 0m,
            CreatedAt: default);
}

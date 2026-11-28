using Humans.Base.Authorization;
using Humans.Permissions.Contracts;
using Humans.Finance.Contracts;
using Humans.Store.Contracts;
using System.Security.Claims;
using Humans.Camps.Contracts;
using Humans.Settings.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Humans.Store.Services.Dtos;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Store.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Store.Tests.Authorization;

/// <summary>
/// Covers the TeamsAdmin branch of <see cref="OrderAuthorizationHandler"/>
/// (read any order, manage team orders only, camp orders view-only) and the
/// product-deadline gate on line edits (Store admins exempt, everyone else denied
/// past <c>OrderableUntil</c>).
/// </summary>
public class OrderAuthorizationHandlerTests
{
    private static readonly LocalDate PastDeadline = new(2026, 1, 1);
    private static readonly LocalDate FutureDeadline = new(2026, 12, 31);

    private readonly ICampService _campService = Substitute.For<ICampService>();
    private readonly IPermissionService _permissions = Substitute.For<IPermissionService>();
    private readonly ISettingsService _shifts = Substitute.For<ISettingsService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 14, 12, 0));
    private readonly OrderAuthorizationHandler _handler;

    public OrderAuthorizationHandlerTests()
    {
        _shifts.GetActiveEventSettings().Returns(BurnFixtures.Burn(year: 2026, timeZoneId: "Europe/Madrid"));
        _users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => UserInfo.Create(new User { Id = call.Arg<Guid>() }, [], [], null));
        _handler = new OrderAuthorizationHandler(_campService, _permissions, _users, _shifts, _clock, new HttpContextAccessor());
    }

    [HumansTheory]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public async Task MembershipAccess_MissingAccount_IsDenied(
        bool teamOrder, bool accountExists, bool survivorOwns)
    {
        var cookieId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        _users.GetUserInfoAsync(cookieId, Arg.Any<CancellationToken>()).Returns(accountExists
            ? UserInfo.Create(new User { Id = survivorId }, [], [], null)
            : null);
        var order = MakeOrder(teamOrder);
        var ownerId = accountExists && survivorOwns ? survivorId : cookieId;
        if (teamOrder)
        {
            _permissions.HasAsync(ownerId, StorePermissions.TeamOrders, new Owner(TeamOwner.Kind, order.TeamId!.Value),
                Arg.Any<CancellationToken>()).Returns(true);
        }
        else
        {
            var camp = MakeCampInfo(Guid.NewGuid(), order.CampSeasonId!.Value, ownerId);
            _campService.GetCampSeasonByIdAsync(order.CampSeasonId.Value, Arg.Any<CancellationToken>())
                .Returns(camp.Seasons[0]);
            _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns([camp]);
        }
        var context = new AuthorizationHandlerContext(
            [OrderOperationRequirement.AddLine], Principal(role: null, cookieId), order);

        await _handler.HandleAsync(context);

        Assert.Equal(accountExists && survivorOwns, context.HasSucceeded);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CampAccess_RequiresLeadershipOfTheOrdersOwnSeason(bool leadsTargetSeason)
    {
        var userId = Guid.NewGuid();
        var campId = Guid.NewGuid();
        var order = MakeOrder(team: false);
        var target = MakeCampInfo(campId, order.CampSeasonId!.Value, leadsTargetSeason ? userId : Guid.NewGuid())
            .Seasons[0] with
        { EventSettingsId = Guid.NewGuid() };
        var other = MakeCampInfo(campId, Guid.NewGuid(), userId).Seasons[0] with { EventSettingsId = Guid.NewGuid() };
        var camp = MakeCampInfo(campId, target.Id, userId) with { Seasons = [target, other] };
        _campService.GetCampSeasonByIdAsync(target.Id, Arg.Any<CancellationToken>()).Returns(target);
        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns([camp]);
        var resources = new (object Resource, OrderOperationRequirement[] Operations)[]
        {
            (order, [OrderOperationRequirement.View, OrderOperationRequirement.Pay, OrderOperationRequirement.EditCounterparty]),
            (new OrderCreateContext(target.Id), [OrderOperationRequirement.Create]),
            (new OrderLineContext(order, FutureDeadline), [OrderOperationRequirement.AddLine, OrderOperationRequirement.RemoveLine])
        };

        foreach (var (resource, operations) in resources)
        {
            var context = new AuthorizationHandlerContext(operations, Principal(role: null, userId), resource);

            await _handler.HandleAsync(context);

            Assert.Equal(leadsTargetSeason, context.HasSucceeded);
            if (!leadsTargetSeason) Assert.Equal(operations.Length, context.PendingRequirements.Count());
        }
    }

    [HumansTheory]
    [InlineData("User")]
    [InlineData("Season")]
    [InlineData("Team")]
    public async Task AuthorizationReads_PropagateAbandonedRequests(string boundary)
    {
        using var request = new CancellationTokenSource();
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestAborted = request.Token } };
        var observed = CancellationToken.None;
        var userId = Guid.NewGuid();
        var order = MakeOrder(team: string.Equals(boundary, "Team", StringComparison.Ordinal));
        var camp = MakeCampInfo(Guid.NewGuid(), order.CampSeasonId ?? Guid.NewGuid(), userId);
        void Read(string name, CancellationToken ct)
        {
            if (string.Equals(name, boundary, StringComparison.Ordinal))
            {
                observed = ct;
                request.Cancel();
            }
            ct.ThrowIfCancellationRequested();
        }
        _shifts.GetActiveEventSettings().Returns(BurnFixtures.Burn(year: 2026, timeZoneId: "Europe/Madrid"));
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            Read("User", call.Arg<CancellationToken>());
            return UserInfo.Create(new User { Id = userId }, [], [], null);
        });
        _campService.GetCampSeasonByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            Read("Season", call.Arg<CancellationToken>());
            return camp.Seasons[0];
        });
        _permissions.HasAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Owner>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            Read("Team", call.Arg<CancellationToken>());
            return false;
        });
        var context = new AuthorizationHandlerContext([OrderOperationRequirement.View], Principal(role: null, userId), order);
        var handler = new OrderAuthorizationHandler(_campService, _permissions, _users, _shifts, _clock, http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.HandleAsync(context));
        Assert.Equal(request.Token, observed);
        Assert.False(context.HasSucceeded);
    }

    [HumansFact]
    public Task View_TeamsAdminCampOrder_IsAllowed() =>
        AssertOutcomeAsync(TeamsRoles.TeamsAdmin, MakeOrder(team: false), OrderOperationRequirement.View, expectAllowed: true);

    [HumansFact]
    public Task Edit_TeamsAdminCampOrder_IsDenied() =>
        AssertOutcomeAsync(TeamsRoles.TeamsAdmin, MakeOrder(team: false), OrderOperationRequirement.AddLine, expectAllowed: false);

    [HumansFact]
    public Task Edit_TeamsAdminTeamOrder_IsAllowed() =>
        AssertOutcomeAsync(TeamsRoles.TeamsAdmin, MakeOrder(team: true), OrderOperationRequirement.AddLine, expectAllowed: true);

    [HumansFact]
    public Task Delete_TeamsAdminTeamOrder_IsAllowed() =>
        AssertOutcomeAsync(TeamsRoles.TeamsAdmin, MakeOrder(team: true), OrderOperationRequirement.Delete, expectAllowed: true);

    [HumansFact]
    public Task CreateTeamOrder_TeamsAdmin_IsAllowed() =>
        // Departments buy from the collective too, and a TeamsAdmin opening the order is how
        // that is tracked — so the grant is deliberately wider than the coordinator's. The
        // principal holds no management role on the team, only TeamsAdmin.
        AssertOutcomeAsync(
            TeamsRoles.TeamsAdmin,
            new OrderCreateContext(CampSeasonId: null, TeamId: Guid.NewGuid()),
            OrderOperationRequirement.Create,
            expectAllowed: true);

    [HumansFact]
    public Task CreateCampOrder_TeamsAdmin_IsDenied() =>
        AssertOutcomeAsync(
            TeamsRoles.TeamsAdmin,
            new OrderCreateContext(CampSeasonId: Guid.NewGuid()),
            OrderOperationRequirement.Create,
            expectAllowed: false);

    [HumansFact]
    public Task Pay_TeamsAdminTeamOrder_IsDenied() =>
        AssertOutcomeAsync(TeamsRoles.TeamsAdmin, MakeOrder(team: true), OrderOperationRequirement.Pay, expectAllowed: false);

    [HumansFact]
    public Task Edit_TeamsAdminIssuedTeamOrder_IsDenied() =>
        AssertOutcomeAsync(TeamsRoles.TeamsAdmin, MakeOrder(team: true, OrderState.InvoiceIssued), OrderOperationRequirement.AddLine, expectAllowed: false);

    [HumansFact]
    public Task AddLine_AdminPastDeadline_IsAllowed() =>
        AssertOutcomeAsync(RoleNames.Admin, new OrderLineContext(MakeOrder(team: false), PastDeadline), OrderOperationRequirement.AddLine, expectAllowed: true);

    [HumansFact]
    public Task RemoveLine_AdminPastDeadline_IsAllowed() =>
        AssertOutcomeAsync(RoleNames.Admin, new OrderLineContext(MakeOrder(team: false), PastDeadline), OrderOperationRequirement.RemoveLine, expectAllowed: true);

    [HumansFact]
    public Task AddLine_TeamsAdminPastDeadline_IsDenied() =>
        AssertOutcomeAsync(TeamsRoles.TeamsAdmin, new OrderLineContext(MakeOrder(team: true), PastDeadline), OrderOperationRequirement.AddLine, expectAllowed: false);

    [HumansFact]
    public Task RemoveLine_TeamsAdminPastDeadline_IsDenied() =>
        AssertOutcomeAsync(TeamsRoles.TeamsAdmin, new OrderLineContext(MakeOrder(team: true), PastDeadline), OrderOperationRequirement.RemoveLine, expectAllowed: false);

    [HumansFact]
    public Task AddLine_TeamsAdminBeforeDeadline_IsAllowed() =>
        AssertOutcomeAsync(TeamsRoles.TeamsAdmin, new OrderLineContext(MakeOrder(team: true), FutureDeadline), OrderOperationRequirement.AddLine, expectAllowed: true);

    [HumansFact]
    public Task AddLine_CampLeadPastDeadline_IsDenied() =>
        AssertLeadOutcomeAsync(PastDeadline, expectAllowed: false);

    [HumansFact]
    public Task AddLine_CampLeadBeforeDeadline_IsAllowed() =>
        AssertLeadOutcomeAsync(FutureDeadline, expectAllowed: true);

    [HumansTheory]
    [InlineData(RoleNames.Admin)]
    [InlineData(StoreRoles.StoreAdmin)]
    [InlineData(FinanceRoles.FinanceAdmin)]
    public async Task Billing_StoreAdminOnTeamOrder_IsDenied(string role)
    {
        // Team orders are non-billable at every privilege level.
        await AssertOutcomeAsync(role, MakeOrder(team: true), OrderOperationRequirement.Pay, expectAllowed: false);
        await AssertOutcomeAsync(role, MakeOrder(team: true), OrderOperationRequirement.EditCounterparty, expectAllowed: false);
        await AssertOutcomeAsync(role, MakeOrder(team: true), OrderOperationRequirement.IssueInvoice, expectAllowed: false);
        await AssertOutcomeAsync(role, MakeOrder(team: true), OrderOperationRequirement.RecordPayment, expectAllowed: false);
    }

    [HumansTheory]
    [InlineData(RoleNames.Admin)]
    [InlineData(StoreRoles.StoreAdmin)]
    [InlineData(FinanceRoles.FinanceAdmin)]
    public Task RecordPayment_StoreAdmin_IsAllowed(string role) =>
        AssertOutcomeAsync(role, MakeOrder(team: false), OrderOperationRequirement.RecordPayment, expectAllowed: true);

    [HumansTheory]
    [InlineData(RoleNames.Admin)]
    [InlineData(FinanceRoles.FinanceAdmin)]
    public Task Refund_FinanceAdminOnCampOrder_IsAllowed(string role) =>
        AssertOutcomeAsync(role, MakeOrder(team: false), OrderOperationRequirement.RecordFinancePayment, expectAllowed: true);

    [HumansFact]
    public Task Refund_StoreAdmin_IsDenied() =>
        AssertOutcomeAsync(StoreRoles.StoreAdmin, MakeOrder(team: false), OrderOperationRequirement.RecordFinancePayment, expectAllowed: false);

    [HumansFact]
    public Task Refund_TeamsAdmin_IsDenied() =>
        AssertOutcomeAsync(TeamsRoles.TeamsAdmin, MakeOrder(team: false), OrderOperationRequirement.RecordFinancePayment, expectAllowed: false);

    [HumansFact]
    public Task Refund_CampLead_IsDenied() =>
        AssertLeadOperationAsync(OrderOperationRequirement.RecordFinancePayment, expectAllowed: false);

    [HumansTheory]
    [InlineData(RoleNames.Admin)]
    [InlineData(FinanceRoles.FinanceAdmin)]
    public Task Refund_TeamOrder_IsDenied(string role) =>
        AssertOutcomeAsync(role, MakeOrder(team: true), OrderOperationRequirement.RecordFinancePayment, expectAllowed: false);

    [HumansFact]
    public Task DeletePayment_AdminOnCampOrder_IsAllowed() =>
        AssertOutcomeAsync(RoleNames.Admin, MakeOrder(team: false), OrderOperationRequirement.DeletePayment, expectAllowed: true);

    [HumansTheory]
    [InlineData(StoreRoles.StoreAdmin)]
    [InlineData(FinanceRoles.FinanceAdmin)]
    [InlineData(TeamsRoles.TeamsAdmin)]
    public Task DeletePayment_NonAdmin_IsDenied(string role) =>
        AssertOutcomeAsync(role, MakeOrder(team: false), OrderOperationRequirement.DeletePayment, expectAllowed: false);

    [HumansFact]
    public Task DeletePayment_CampLead_IsDenied() =>
        AssertLeadOperationAsync(OrderOperationRequirement.DeletePayment, expectAllowed: false);

    [HumansFact]
    public Task RecordPayment_CampLead_IsDenied() =>
        AssertLeadOperationAsync(OrderOperationRequirement.RecordPayment, expectAllowed: false);

    [HumansFact]
    public Task RecordPayment_TeamsAdmin_IsDenied() =>
        AssertOutcomeAsync(TeamsRoles.TeamsAdmin, MakeOrder(team: false), OrderOperationRequirement.RecordPayment, expectAllowed: false);

    [HumansFact]
    public Task IssueInvoice_StoreAdmin_IsAllowed() =>
        AssertOutcomeAsync(StoreRoles.StoreAdmin, MakeOrder(team: false), OrderOperationRequirement.IssueInvoice, expectAllowed: true);

    [HumansFact]
    public Task IssueInvoice_CampLead_IsDenied() =>
        AssertLeadOperationAsync(OrderOperationRequirement.IssueInvoice, expectAllowed: false);

    [HumansFact]
    public Task Delete_CampLead_IsDenied() =>
        AssertLeadOperationAsync(OrderOperationRequirement.Delete, expectAllowed: false);

    [HumansFact]
    public Task Pay_CampLeadOwnOrder_IsAllowed() =>
        AssertLeadOperationAsync(OrderOperationRequirement.Pay, expectAllowed: true);

    [HumansTheory]
    [MemberData(nameof(LeadOperations))]
    public async Task AnyOperation_OtherCampLead_IsDenied(string operation)
    {
        var requirement = LeadOperationsByName[operation];
        var order = MakeOrder(team: false);
        var seasonId = order.CampSeasonId!.Value;
        var otherCampsLead = Guid.NewGuid();
        var camp = MakeCampInfo(Guid.NewGuid(), seasonId, leadUserId: otherCampsLead);
        _campService.GetCampSeasonByIdAsync(seasonId, Arg.Any<CancellationToken>())
            .Returns(camp.Seasons[0]);
        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([camp]);

        var context = new AuthorizationHandlerContext([requirement], Principal(role: null, Guid.NewGuid()), order);

        await _handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    private static readonly Dictionary<string, OrderOperationRequirement> LeadOperationsByName = new(StringComparer.Ordinal)
    {
        [nameof(OrderOperationRequirement.View)] = OrderOperationRequirement.View,
        [nameof(OrderOperationRequirement.AddLine)] = OrderOperationRequirement.AddLine,
        [nameof(OrderOperationRequirement.RemoveLine)] = OrderOperationRequirement.RemoveLine,
        [nameof(OrderOperationRequirement.EditCounterparty)] = OrderOperationRequirement.EditCounterparty,
        [nameof(OrderOperationRequirement.Pay)] = OrderOperationRequirement.Pay,
    };

    public static TheoryData<string> LeadOperations => new(LeadOperationsByName.Keys);

    [HumansTheory]
    [InlineData(nameof(OrderOperationRequirement.AddLine))]
    [InlineData(nameof(OrderOperationRequirement.RemoveLine))]
    [InlineData(nameof(OrderOperationRequirement.EditCounterparty))]
    public Task Edit_CampLeadIssuedOrder_IsDenied(string operation) =>
        AssertLeadOperationAsync(LeadOperationsByName[operation], expectAllowed: false, OrderState.InvoiceIssued);

    [HumansFact]
    public Task Pay_CampLeadIssuedOrder_IsAllowed() =>
        AssertLeadOperationAsync(OrderOperationRequirement.Pay, expectAllowed: true, OrderState.InvoiceIssued);

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task IssueInvoice_TeamsAdmin_IsDenied(bool teamOrder) =>
        AssertOutcomeAsync(TeamsRoles.TeamsAdmin, MakeOrder(teamOrder), OrderOperationRequirement.IssueInvoice, expectAllowed: false);

    private async Task AssertOutcomeAsync(
        string role,
        object resource,
        OrderOperationRequirement requirement,
        bool expectAllowed)
    {
        var context = new AuthorizationHandlerContext([requirement], Principal(role), resource);

        await _handler.HandleAsync(context);

        Assert.Equal(expectAllowed, context.HasSucceeded);
    }

    private async Task AssertLeadOutcomeAsync(LocalDate deadline, bool expectAllowed)
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
            [OrderOperationRequirement.AddLine],
            Principal(role: null, userId),
            new OrderLineContext(order, deadline));

        await _handler.HandleAsync(context);

        Assert.Equal(expectAllowed, context.HasSucceeded);
    }

    private async Task AssertLeadOperationAsync(
        OrderOperationRequirement requirement, bool expectAllowed, OrderState state = OrderState.Open)
    {
        var userId = Guid.NewGuid();
        var order = MakeOrder(team: false, state);
        var seasonId = order.CampSeasonId!.Value;
        var camp = MakeCampInfo(Guid.NewGuid(), seasonId, userId);
        _campService.GetCampSeasonByIdAsync(seasonId, Arg.Any<CancellationToken>())
            .Returns(camp.Seasons[0]);
        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([camp]);

        var context = new AuthorizationHandlerContext([requirement], Principal(role: null, userId), order);

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

    private static OrderDto MakeOrder(bool team, OrderState state = OrderState.Open) =>
        new(
            Id: Guid.NewGuid(),
            CampSeasonId: team ? null : Guid.NewGuid(),
            TeamId: team ? Guid.NewGuid() : null,
            CounterpartyType: team ? OrderCounterpartyType.Team : OrderCounterpartyType.Camp,
            CounterpartyDisplayName: "x",
            Year: 2026,
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

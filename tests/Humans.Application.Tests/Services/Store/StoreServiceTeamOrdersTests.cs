using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Store;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Services.Store;
using Humans.Application.Services.Store.Dtos;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Store;

/// <summary>
/// Focused coverage for the polymorphic team-coordinator order path.
/// See <c>docs/superpowers/specs/2026-05-27-store-team-orders-design.md</c>.
/// </summary>
public class StoreServiceTeamOrdersTests
{
    private readonly IStoreRepository _repo = Substitute.For<IStoreRepository>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly ICampServiceRead _campService = Substitute.For<ICampServiceRead>();
    private readonly ITeamServiceRead _teams = Substitute.For<ITeamServiceRead>();
    private readonly IShiftManagementService _shifts = Substitute.For<IShiftManagementService>();
    private readonly IStripeService _stripe = Substitute.For<IStripeService>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 14, 12, 0));
    private readonly StoreService _service;

    public StoreServiceTeamOrdersTests()
    {
        _shifts.GetActiveAsync().Returns(new EventSettings
        {
            Year = 2026,
            TimeZoneId = "Europe/Madrid"
        });
        _teams.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());
        _campService.GetCampsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>());
        _service = new StoreService(_repo, _audit, _campService, _teams, _clock, _shifts, _stripe, NullLogger<StoreService>.Instance);
    }

    // ==========================================================================
    // CreateTeamOrderAsync
    // ==========================================================================

    [HumansFact]
    public async Task CreateTeamOrderAsync_writes_order_with_team_id_and_active_year()
    {
        var teamId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _teams.GetTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(MakeDepartment(teamId, "Kitchen", userId));
        _repo.GetOrderForTeamAsync(teamId, 2026, Arg.Any<CancellationToken>())
            .Returns((StoreOrder?)null);

        StoreOrder? captured = null;
        await _repo.AddOrderAsync(Arg.Do<StoreOrder>(o => captured = o), Arg.Any<CancellationToken>());

        var id = await _service.CreateTeamOrderAsync(teamId, userId);

        captured.Should().NotBeNull();
        captured!.Id.Should().Be(id);
        captured.TeamId.Should().Be(teamId);
        captured.CampSeasonId.Should().BeNull();
        captured.Year.Should().Be(2026);
        captured.State.Should().Be(StoreOrderState.Open);
    }

    [HumansFact]
    public async Task CreateTeamOrderAsync_throws_when_team_already_has_order_this_year()
    {
        var teamId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _teams.GetTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(MakeDepartment(teamId, "Kitchen", userId));
        _repo.GetOrderForTeamAsync(teamId, 2026, Arg.Any<CancellationToken>())
            .Returns(new StoreOrder { Id = Guid.NewGuid(), TeamId = teamId, Year = 2026 });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateTeamOrderAsync(teamId, userId));
    }

    [HumansFact]
    public async Task CreateTeamOrderAsync_throws_when_team_is_a_subteam()
    {
        var teamId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        _teams.GetTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(MakeDepartment(teamId, "Sub", userId, parentTeamId: parentId));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateTeamOrderAsync(teamId, userId));
    }

    [HumansFact]
    public async Task CreateTeamOrderAsync_throws_when_team_not_found()
    {
        var teamId = Guid.NewGuid();
        _teams.GetTeamAsync(teamId, Arg.Any<CancellationToken>()).Returns((TeamInfo?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateTeamOrderAsync(teamId, Guid.NewGuid()));
    }

    // ==========================================================================
    // Guard rails on billable-only methods
    // ==========================================================================

    [HumansFact]
    public async Task UpdateCounterpartyAsync_throws_on_team_order()
    {
        var orderId = Guid.NewGuid();
        var teamOrder = new StoreOrder { Id = orderId, TeamId = Guid.NewGuid(), CampSeasonId = null, Year = 2026 };
        _repo.GetOrderByIdAsync(orderId, Arg.Any<CancellationToken>()).Returns(teamOrder);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.UpdateCounterpartyAsync(
                orderId,
                new OrderCounterpartyInput("N", null, null, null, null),
                Guid.NewGuid()));
    }

    [HumansFact]
    public async Task CreateStripeCheckoutSessionAsync_throws_on_team_order()
    {
        var teamOrder = MakeOrderDto(
            counterpartyType: StoreOrderCounterpartyType.Team,
            balanceEur: 0m);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.CreateStripeCheckoutSessionAsync(teamOrder, 10m, "https://x"));
    }

    [HumansFact]
    public async Task RecordStripePaymentAsync_throws_on_team_order()
    {
        var orderId = Guid.NewGuid();
        var teamOrder = new StoreOrder { Id = orderId, TeamId = Guid.NewGuid(), CampSeasonId = null, Year = 2026 };
        _repo.GetOrderByIdAsync(orderId, Arg.Any<CancellationToken>()).Returns(teamOrder);
        _repo.StripePaymentIntentExistsAsync("pi_x", Arg.Any<CancellationToken>()).Returns(false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RecordStripePaymentAsync(orderId, "pi_x", 10m));
    }

    // ==========================================================================
    // Year backfill on legacy camp orders
    // ==========================================================================

    [HumansFact]
    public async Task AddLineAsync_backfills_Year_from_camp_season_when_order_year_is_zero()
    {
        var orderId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var product = new StoreProduct
        {
            Id = Guid.NewGuid(),
            Year = 2025,
            IsActive = true,
            OrderableUntil = new LocalDate(2026, 12, 31),
            UnitPriceEur = 10m,
            VatRatePercent = 21m,
        };
        var order = new StoreOrder
        {
            Id = orderId,
            CampSeasonId = seasonId,
            TeamId = null,
            Year = 0,
            State = StoreOrderState.Open,
        };
        _repo.GetOrderByIdAsync(orderId, Arg.Any<CancellationToken>()).Returns(order);
        _repo.GetProductByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);
        _campService.GetCampSeasonByIdAsync(seasonId, Arg.Any<CancellationToken>())
            .Returns(new CampSeasonInfo(seasonId, Guid.NewGuid(), "alpha", 2025, null,
                "Camp X", string.Empty, string.Empty, [], CampSeasonStatus.Pending,
                YesNoMaybe.No, YesNoMaybe.No, AdultPlayspacePolicy.No, 0, null, null, null, 0, null, null));

        await _service.AddLineAsync(orderId, product.Id, 1, Guid.NewGuid());

        await _repo.Received(1).UpdateOrderAsync(
            Arg.Is<StoreOrder>(o => o.Year == 2025),
            Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // Index data — team coordinators see their departments
    // ==========================================================================

    [HumansFact]
    public async Task GetIndexDataAsync_lists_coordinated_departments_alongside_led_camps()
    {
        var userId = Guid.NewGuid();
        var deptId = Guid.NewGuid();
        var subteamId = Guid.NewGuid();
        _teams.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>
            {
                [deptId] = MakeDepartment(deptId, "Build", userId),
                // sub-team the user coordinates — must NOT appear in the index
                [subteamId] = MakeDepartment(subteamId, "Build Sub", userId, parentTeamId: deptId),
            });
        _repo.GetOrderForTeamAsync(deptId, 2026, Arg.Any<CancellationToken>()).Returns((StoreOrder?)null);
        _repo.GetActiveProductsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<StoreProduct>());

        var data = await _service.GetIndexDataAsync(userId, isPrivilegedReader: false);

        data.Counterparties.Should().HaveCount(1);
        data.Counterparties[0].CounterpartyType.Should().Be(StoreOrderCounterpartyType.Team);
        data.Counterparties[0].CounterpartyId.Should().Be(deptId);
        data.Counterparties[0].Orders.Should().BeEmpty();
        data.ShowNoOrdersMessage.Should().BeFalse();
    }

    [HumansFact]
    public async Task GetIndexDataAsync_ignores_departments_user_does_not_coordinate()
    {
        var userId = Guid.NewGuid();
        var otherDeptId = Guid.NewGuid();
        _teams.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>
            {
                [otherDeptId] = MakeDepartment(otherDeptId, "Other", coordinatorUserId: Guid.NewGuid()),
            });
        _repo.GetActiveProductsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<StoreProduct>());

        var data = await _service.GetIndexDataAsync(userId, isPrivilegedReader: false);

        data.Counterparties.Should().BeEmpty();
        data.ShowNoOrdersMessage.Should().BeTrue();
    }

    // ==========================================================================
    // Summary — team orders merge into cross-tab
    // ==========================================================================

    [HumansFact]
    public async Task GetStoreSummaryAsync_merges_team_orders_into_by_counterparty_and_cross_tab()
    {
        var deptId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([]);
        _teams.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>
            {
                [deptId] = MakeDepartment(deptId, "Kitchen", Guid.NewGuid()),
            });
        _repo.GetAllProductsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns([
            new StoreProduct {
                Id = productId, Year = 2026, Name = "Tent", Description = "x",
                UnitPriceEur = 10m, VatRatePercent = 0m,
                OrderableUntil = new LocalDate(2026,12,31), IsActive = true }
        ]);
        _repo.GetOrdersForTeamsWithLinesAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(deptId)),
                2026, Arg.Any<CancellationToken>())
            .Returns([new StoreOrder {
                Id = orderId, TeamId = deptId, Year = 2026, State = StoreOrderState.Open,
                Lines = { new StoreOrderLine {
                    Id = Guid.NewGuid(), OrderId = orderId, ProductId = productId,
                    Qty = 7, UnitPriceSnapshot = 10m, VatRateSnapshot = 0m }} }]);

        var result = await _service.GetStoreSummaryAsync(2026);

        result.ByCounterparty.Should().ContainSingle(r =>
            r.CounterpartyType == StoreOrderCounterpartyType.Team
            && r.OrderId == orderId
            && r.CounterpartyName == "Kitchen"
            && r.PaymentsTotalEur == 0m
            && r.BalanceEur == 0m);
        result.ByItem.Single(i => i.ProductId == productId).TotalQty.Should().Be(7);
        result.CrossTab.Counterparties.Should().ContainSingle(r =>
            r.CounterpartyType == StoreOrderCounterpartyType.Team
            && r.CounterpartyName == "Kitchen"
            && r.TotalQty == 7);
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private static TeamInfo MakeDepartment(
        Guid teamId,
        string name,
        Guid coordinatorUserId,
        Guid? parentTeamId = null)
    {
        return new TeamInfo(
            Id: teamId,
            Name: name,
            Description: null,
            Slug: name.ToLowerInvariant(),
            IsActive: true,
            IsSystemTeam: false,
            SystemTeamType: SystemTeamType.None,
            RequiresApproval: false,
            IsPublicPage: true,
            IsHidden: false,
            IsPromotedToDirectory: false,
            CreatedAt: Instant.FromUtc(2026, 1, 1, 0, 0),
            Members: new List<TeamMemberInfo>(),
            ParentTeamId: parentTeamId,
            ManagementRoleHolderUserIds: new HashSet<Guid> { coordinatorUserId });
    }

    // ==========================================================================
    // DeleteOrderAsync
    // ==========================================================================

    [HumansFact]
    public async Task DeleteOrderAsync_removes_zero_balance_order_and_audits()
    {
        var orderId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        _repo.GetOrderWithLinesAndPaymentsAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(new StoreOrder { Id = orderId, CampSeasonId = Guid.NewGuid(), Year = 2026 });

        await _service.DeleteOrderAsync(orderId, actor);

        await _repo.Received(1).DeleteOrderAsync(orderId, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.StoreOrderDeleted, nameof(StoreOrder), orderId,
            Arg.Any<string>(), actor,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task DeleteOrderAsync_rejects_non_zero_balance_camp_order()
    {
        var orderId = Guid.NewGuid();
        var order = new StoreOrder
        {
            Id = orderId,
            CampSeasonId = Guid.NewGuid(),
            Year = 2026,
            Lines = new List<StoreOrderLine>
            {
                new() { Id = Guid.NewGuid(), OrderId = orderId, ProductId = Guid.NewGuid(),
                        Qty = 1, UnitPriceSnapshot = 10m, VatRateSnapshot = 0m }
            }
        };
        _repo.GetOrderWithLinesAndPaymentsAsync(orderId, Arg.Any<CancellationToken>()).Returns(order);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.DeleteOrderAsync(orderId, Guid.NewGuid()));

        await _repo.DidNotReceive().DeleteOrderAsync(orderId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task DeleteOrderAsync_throws_when_order_not_found()
    {
        var orderId = Guid.NewGuid();
        _repo.GetOrderWithLinesAndPaymentsAsync(orderId, Arg.Any<CancellationToken>())
            .Returns((StoreOrder?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.DeleteOrderAsync(orderId, Guid.NewGuid()));
    }

    // ==========================================================================
    // CreateOrderAsync uniqueness (camp side — one order per camp-season-year)
    // ==========================================================================

    [HumansFact]
    public async Task CreateOrderAsync_throws_when_camp_season_already_has_order_for_year()
    {
        var seasonId = Guid.NewGuid();
        _campService.GetCampSeasonByIdAsync(seasonId, Arg.Any<CancellationToken>())
            .Returns(new CampSeasonInfo(seasonId, Guid.NewGuid(), "alpha", 2026, null,
                "Camp X", string.Empty, string.Empty, [], CampSeasonStatus.Pending,
                YesNoMaybe.No, YesNoMaybe.No, AdultPlayspacePolicy.No, 0, null, null, null, 0, null, null));
        _repo.GetOrdersForCampSeasonAsync(seasonId, Arg.Any<CancellationToken>())
            .Returns(new List<StoreOrder>
            {
                new() { Id = Guid.NewGuid(), CampSeasonId = seasonId, Year = 2026 }
            });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateOrderAsync(seasonId, null, Guid.NewGuid()));
    }

    private static OrderDto MakeOrderDto(
        StoreOrderCounterpartyType counterpartyType,
        decimal balanceEur)
    {
        return new OrderDto(
            Id: Guid.NewGuid(),
            CampSeasonId: counterpartyType == StoreOrderCounterpartyType.Camp ? Guid.NewGuid() : null,
            TeamId: counterpartyType == StoreOrderCounterpartyType.Team ? Guid.NewGuid() : null,
            CounterpartyType: counterpartyType,
            CounterpartyDisplayName: counterpartyType.ToString(),
            Year: 2026,
            Label: null,
            State: StoreOrderState.Open,
            CounterpartyName: null,
            CounterpartyVatId: null,
            CounterpartyAddress: null,
            CounterpartyCountryCode: null,
            CounterpartyEmail: null,
            IssuedInvoiceId: null,
            Lines: [],
            LinesSubtotalEur: balanceEur,
            VatTotalEur: 0m,
            DepositTotalEur: 0m,
            PaymentsTotalEur: 0m,
            BalanceEur: balanceEur);
    }
}

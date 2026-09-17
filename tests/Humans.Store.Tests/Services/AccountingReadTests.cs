using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Enums;
using Humans.Camps.Contracts;
using Humans.Holded.Contracts;
using Humans.Shifts.Contracts;
using Humans.Store.Contracts;
using Humans.Store.Data;
using Humans.Store.Domain;
using Humans.Store.Services;
using Humans.Stripe.Contracts;
using Humans.Teams.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Store.Tests.Services;

/// <summary>
/// The accounting export behind <c>/api/backdoor/store</c> (peterdrier/Humans#1719): every
/// line of the year's camp and team orders priced as the admin summary prices them, and
/// every settled camp payment. Camp and team labels are stitched here, not by the caller.
/// </summary>
public class AccountingReadTests
{
    private static readonly Guid SeasonId = Guid.NewGuid();
    private static readonly Guid DeptId = Guid.NewGuid();
    private static readonly Guid IceId = Guid.NewGuid();
    private static readonly Guid PalletId = Guid.NewGuid();
    private static readonly Guid CampOrderId = Guid.NewGuid();
    private static readonly Guid TeamOrderId = Guid.NewGuid();

    private readonly IStoreRepository _repo = Substitute.For<IStoreRepository>();
    private readonly ICampServiceRead _camps = Substitute.For<ICampServiceRead>();
    private readonly ITeamServiceRead _teams = Substitute.For<ITeamServiceRead>();
    private readonly Service _service;

    public AccountingReadTests()
    {
        _service = new Service(
            _repo, Substitute.For<IAuditLogService>(), _camps, _teams,
            new FakeClock(Instant.FromUtc(2026, 9, 1, 12, 0)), Substitute.For<IBurnSettingsService>(),
            Substitute.For<IStripeService>(), Substitute.For<IHoldedClient>(),
            Options.Create(new StoreSectionOptions()), NullLogger<Service>.Instance);

        _camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns([MakeCampInfo(SeasonId, "Camp Alpha")]);
        _teams.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo> { [DeptId] = MakeTeam(DeptId, "Ice Crew") });

        // Live catalog for 2026: ice was repriced after the camp line was added.
        var ice = new Product
        {
            Id = IceId,
            Year = 2026,
            Name = "Ice",
            Description = "x",
            UnitPriceEur = 4m,
            VatRatePercent = 10m,
            HoldedRevenueAccountNum = 75900002,
            OrderableUntil = new LocalDate(2026, 12, 31),
            IsActive = true,
        };
        var pallet = new Product
        {
            Id = PalletId,
            Year = 2026,
            Name = "Pallet",
            Description = "x",
            UnitPriceEur = 10m,
            VatRatePercent = 21m,
            DepositAmountEur = 5m,
            OrderableUntil = new LocalDate(2026, 12, 31),
            IsActive = true,
        };
        _repo.GetAllProductsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns([ice, pallet]);
        _repo.GetProductsByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([ice, pallet]);
        _repo.GetInvoicesForOrdersAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var campOrder = new Order
        {
            Id = CampOrderId,
            CampSeasonId = SeasonId,
            Year = 2026,
            State = OrderState.Open,
            CounterpartyName = "Alpha SL",
            CounterpartyVatId = "B12345678",
            CounterpartyCountryCode = "ES",
            Lines =
            {
                new OrderLine { Id = Guid.NewGuid(), OrderId = CampOrderId, ProductId = IceId, Qty = 3, UnitPriceSnapshot = 3m, VatRateSnapshot = 10m, AddedAt = Instant.FromUtc(2026, 6, 1, 0, 0) },
                new OrderLine { Id = Guid.NewGuid(), OrderId = CampOrderId, ProductId = PalletId, Qty = 2, UnitPriceSnapshot = 10m, VatRateSnapshot = 21m, DepositAmountSnapshot = 5m, AddedAt = Instant.FromUtc(2026, 6, 2, 0, 0) },
            },
            Payments =
            {
                new Payment { Id = Guid.NewGuid(), OrderId = CampOrderId, AmountEur = 50m, Method = PaymentMethod.Stripe, Status = PaymentStatus.Paid, StripePaymentIntentId = "pi_1", ReceivedAt = Instant.FromUtc(2026, 6, 3, 0, 0) },
                new Payment { Id = Guid.NewGuid(), OrderId = CampOrderId, AmountEur = -10m, Method = PaymentMethod.Manual, Status = PaymentStatus.Paid, ExternalRef = "refund-1", ReceivedAt = Instant.FromUtc(2026, 6, 4, 0, 0) },
                new Payment { Id = Guid.NewGuid(), OrderId = CampOrderId, AmountEur = 30m, Method = PaymentMethod.Stripe, Status = PaymentStatus.Pending, StripePaymentIntentId = "pi_2", ReceivedAt = Instant.FromUtc(2026, 6, 5, 0, 0) },
                new Payment { Id = Guid.NewGuid(), OrderId = CampOrderId, AmountEur = 30m, Method = PaymentMethod.Stripe, Status = PaymentStatus.Failed, StripePaymentIntentId = "pi_3", ReceivedAt = Instant.FromUtc(2026, 6, 6, 0, 0) },
            },
        };
        _repo.GetOrdersForCampSeasonsWithLinesAndPaymentsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(SeasonId)), Arg.Any<CancellationToken>())
            .Returns([campOrder]);

        var teamOrder = new Order
        {
            Id = TeamOrderId,
            TeamId = DeptId,
            Year = 2026,
            State = OrderState.Open,
            Lines = { new OrderLine { Id = Guid.NewGuid(), OrderId = TeamOrderId, ProductId = IceId, Qty = 5, UnitPriceSnapshot = 3m, VatRateSnapshot = 10m } },
        };
        _repo.GetOrdersForTeamsWithLinesAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(DeptId)), 2026, Arg.Any<CancellationToken>())
            .Returns([teamOrder]);

        // The export selects by persisted Year, not through the counterparty.
        _repo.GetOrdersForYearWithLinesAndPaymentsAsync(2026, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([campOrder, teamOrder]);
    }

    [HumansFact]
    public async Task Order_lines_cover_camp_and_team_orders_with_their_labels()
    {
        var lines = await _service.GetOrderLinesAsync(2026, Xunit.TestContext.Current.CancellationToken);

        lines.Should().HaveCount(3);
        lines.Where(l => l.OrderId == CampOrderId).Should().AllSatisfy(l =>
        {
            l.CounterpartyType.Should().Be(OrderCounterpartyType.Camp);
            l.CounterpartyLabel.Should().Be("Camp Alpha");
            l.CounterpartyName.Should().Be("Alpha SL");
            l.CounterpartyVatId.Should().Be("B12345678");
            l.CounterpartyCountryCode.Should().Be("ES");
            l.Year.Should().Be(2026);
        });
        var team = lines.Single(l => l.OrderId == TeamOrderId);
        team.CounterpartyType.Should().Be(OrderCounterpartyType.Team);
        team.CounterpartyLabel.Should().Be("Ice Crew");
        team.CounterpartyName.Should().BeNull();
        team.ProductName.Should().Be("Ice");
        team.HoldedRevenueAccountNum.Should().Be(75900002);
    }

    [HumansFact]
    public async Task Order_lines_are_priced_like_the_summary_so_gross_sums_to_the_order_total()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        var lines = await _service.GetOrderLinesAsync(2026, ct);
        var summary = await _service.GetStoreSummaryAsync(2026, ct);

        // Open orders reprice to the live catalog: ice is 4 now, not the 3 snapshotted.
        var ice = lines.Single(l => l.OrderId == CampOrderId && l.ProductId == IceId);
        ice.UnitPrice.Should().Be(4m);
        ice.VatRatePercent.Should().Be(10m);
        ice.LineNet.Should().Be(12m);
        ice.LineVat.Should().Be(1.20m);
        ice.DepositAmount.Should().Be(0m);
        ice.LineGross.Should().Be(13.20m);

        var pallet = lines.Single(l => l.OrderId == CampOrderId && l.ProductId == PalletId);
        pallet.LineNet.Should().Be(20m);
        pallet.LineVat.Should().Be(4.20m);
        pallet.DepositAmount.Should().Be(10m);
        pallet.LineGross.Should().Be(34.20m);

        foreach (var order in summary.ByCounterparty)
            lines.Where(l => l.OrderId == order.OrderId).Sum(l => l.LineGross).Should().Be(order.TotalDueEur);
    }

    [HumansFact]
    public async Task Order_lines_carry_the_issued_invoice_number_and_frozen_state()
    {
        _repo.GetInvoicesForOrdersAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(CampOrderId)), Arg.Any<CancellationToken>())
            .Returns([new Invoice { Id = Guid.NewGuid(), OrderId = CampOrderId, HoldedDocId = "h1", HoldedDocNumber = "F-2026-0007" }]);
        var campOrder = (await _repo.GetOrdersForCampSeasonsWithLinesAndPaymentsAsync([SeasonId], CancellationToken.None))[0];
        campOrder.State = OrderState.InvoiceIssued;
        campOrder.IssuedInvoiceId = Guid.NewGuid();

        var lines = await _service.GetOrderLinesAsync(2026, Xunit.TestContext.Current.CancellationToken);

        var ice = lines.Single(l => l.OrderId == CampOrderId && l.ProductId == IceId);
        ice.OrderState.Should().Be(OrderState.InvoiceIssued);
        ice.IssuedInvoiceNumber.Should().Be("F-2026-0007");
        ice.UnitPrice.Should().Be(3m, "an issued order is frozen at its snapshots");
        lines.Single(l => l.OrderId == TeamOrderId).IssuedInvoiceNumber.Should().BeNull();
    }

    [HumansFact]
    public async Task Payments_are_paid_only_refunds_negative_and_carry_the_stripe_intent()
    {
        var payments = await _service.GetPaymentsAsync(2026, Xunit.TestContext.Current.CancellationToken);

        payments.Should().HaveCount(2, "Pending and Failed rows are not money");
        payments.Should().AllSatisfy(p =>
        {
            p.OrderId.Should().Be(CampOrderId);
            p.CounterpartyType.Should().Be(OrderCounterpartyType.Camp);
            p.CounterpartyLabel.Should().Be("Camp Alpha");
            p.Year.Should().Be(2026);
        });
        var stripe = payments.Single(p => string.Equals(p.StripePaymentIntentId, "pi_1", StringComparison.Ordinal));
        stripe.AmountEur.Should().Be(50m);
        stripe.Method.Should().Be("Stripe");
        var refund = payments.Single(p => string.Equals(p.ExternalRef, "refund-1", StringComparison.Ordinal));
        refund.AmountEur.Should().Be(-10m);
        refund.Method.Should().Be("Manual");
        refund.StripePaymentIntentId.Should().BeNull();
    }

    [HumansFact]
    public async Task Orders_whose_camp_was_deleted_since_still_export_with_their_money()
    {
        // The camp is gone (seasons cascade; Store keeps the bare CampSeasonId), so it is
        // absent from GetCampsForYearAsync — the order must still appear, unlabeled.
        var orphanId = Guid.NewGuid();
        var orphan = new Order
        {
            Id = orphanId,
            CampSeasonId = Guid.NewGuid(),
            Year = 2026,
            State = OrderState.Open,
            Lines = { new OrderLine { Id = Guid.NewGuid(), OrderId = orphanId, ProductId = IceId, Qty = 1, UnitPriceSnapshot = 3m, VatRateSnapshot = 10m } },
            Payments = { new Payment { Id = Guid.NewGuid(), OrderId = orphanId, AmountEur = 4.40m, Method = PaymentMethod.Stripe, Status = PaymentStatus.Paid, StripePaymentIntentId = "pi_orphan" } },
        };
        _repo.GetOrdersForYearWithLinesAndPaymentsAsync(2026, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([orphan]);

        var ct = Xunit.TestContext.Current.CancellationToken;
        var line = (await _service.GetOrderLinesAsync(2026, ct)).Single();
        var payment = (await _service.GetPaymentsAsync(2026, ct)).Single();

        line.OrderId.Should().Be(orphanId);
        line.CounterpartyType.Should().Be(OrderCounterpartyType.Camp);
        line.CounterpartyLabel.Should().Be("(unknown camp)");
        payment.StripePaymentIntentId.Should().Be("pi_orphan");
        payment.CounterpartyLabel.Should().Be("(unknown camp)");
    }

    [HumansFact]
    public async Task Empty_year_returns_no_rows()
    {
        _camps.GetCampsForYearAsync(2025, Arg.Any<CancellationToken>()).Returns([]);
        _repo.GetAllProductsForYearAsync(2025, Arg.Any<CancellationToken>()).Returns([]);
        _repo.GetOrdersForYearWithLinesAndPaymentsAsync(2025, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var ct = Xunit.TestContext.Current.CancellationToken;
        (await _service.GetOrderLinesAsync(2025, ct)).Should().BeEmpty();
        (await _service.GetPaymentsAsync(2025, ct)).Should().BeEmpty();
    }

    private static CampInfo MakeCampInfo(Guid seasonId, string name)
    {
        var campId = Guid.NewGuid();
        return new CampInfo(
            campId, "alpha", "camp@example.com", "+34600000000", IsSwissCamp: false, TimesAtNowhere: 0,
            Seasons:
            [
                new CampSeasonInfo(
                    seasonId, campId, "alpha", 2026, null, name, string.Empty, string.Empty, [],
                    CampSeasonStatus.Active, YesNoMaybe.No, YesNoMaybe.No, AdultPlayspacePolicy.No,
                    0, null, null, null, 0, null, null)
            ]);
    }

    private static TeamInfo MakeTeam(Guid teamId, string name) =>
        new(
            Id: teamId, Name: name, Description: null, Slug: name.ToLowerInvariant(),
            IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None, RequiresApproval: false,
            IsPublicPage: true, IsHidden: false, IsPromotedToDirectory: false,
            CreatedAt: Instant.FromUtc(2026, 1, 1, 0, 0), Members: new List<TeamMemberInfo>());
}

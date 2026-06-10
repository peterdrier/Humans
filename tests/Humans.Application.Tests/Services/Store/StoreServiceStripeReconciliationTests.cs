using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Services.Store;
using Humans.Application.Services.Store.Dtos;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services.Store;

public class StoreServiceStripeReconciliationTests
{
    private readonly IStoreRepository _repo = Substitute.For<IStoreRepository>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly ICampServiceRead _campService = Substitute.For<ICampServiceRead>();
    private readonly ITeamServiceRead _teams = Substitute.For<ITeamServiceRead>();
    private readonly IShiftManagementService _shifts = Substitute.For<IShiftManagementService>();
    private readonly IStripeService _stripe = Substitute.For<IStripeService>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 6, 4, 12, 0));
    private readonly StoreService _service;

    public StoreServiceStripeReconciliationTests()
    {
        _shifts.GetActiveAsync().Returns(new EventSettings { Year = 2026, TimeZoneId = "Europe/Madrid" });
        // Mapping-chain stubs so GetOrderAsync resolves without throwing.
        _repo.GetAllProductsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<StoreProduct>());
        _repo.GetProductNamesByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>());
        _service = new StoreService(_repo, _audit, _campService, _teams, _clock, _shifts, _stripe, NullLogger<StoreService>.Instance);
    }

    private static StoreOrder CampOrder(Guid id) => new()
    {
        Id = id,
        CampSeasonId = Guid.NewGuid(),
        TeamId = null,
        Year = 2026,
        State = StoreOrderState.Open,
        Lines = [],
        Payments = [],
    };

    private static StoreOrder TeamOrder(Guid id) => new()
    {
        Id = id,
        CampSeasonId = null,
        TeamId = Guid.NewGuid(),
        Year = 2026,
        State = StoreOrderState.Open,
        Lines = [],
        Payments = [],
    };

    private static StoreCheckoutSessionData Session(
        string sessionId, Guid? orderId, string? pi, decimal? amount, string status) =>
        new(sessionId, orderId, pi, amount, status, Instant.FromUtc(2026, 6, 1, 9, 0));

    [HumansFact]
    public async Task GetStripeReconciliationAsync_classifies_each_session()
    {
        var recordedOrder = Guid.NewGuid();
        var missingOrder = Guid.NewGuid();
        var teamOrder = Guid.NewGuid();

        _stripe.ListStoreCheckoutSessionsAsync(Arg.Any<CancellationToken>()).Returns(new List<StoreCheckoutSessionData>
        {
            Session("cs_recorded", recordedOrder, "pi_recorded", 100m, "paid"),
            Session("cs_missing", missingOrder, "pi_missing", 1800m, "paid"),
            Session("cs_team", teamOrder, "pi_team", 50m, "paid"),
            Session("cs_nometa", null, "pi_nometa", 75m, "paid"),
            Session("cs_unpaid", missingOrder, null, 200m, "unpaid"),
        });
        _repo.GetRecordedStripePaymentsAsync(Arg.Any<CancellationToken>()).Returns(new List<StoreRecordedStripePayment>
        {
            new("pi_recorded", recordedOrder, 100m, Instant.FromUtc(2026, 5, 1, 0, 0)),
        });
        _repo.GetOrderWithLinesAndPaymentsAsync(recordedOrder, Arg.Any<CancellationToken>()).Returns(CampOrder(recordedOrder));
        _repo.GetOrderWithLinesAndPaymentsAsync(missingOrder, Arg.Any<CancellationToken>()).Returns(CampOrder(missingOrder));
        _repo.GetOrderWithLinesAndPaymentsAsync(teamOrder, Arg.Any<CancellationToken>()).Returns(TeamOrder(teamOrder));

        var report = await _service.GetStripeReconciliationAsync();

        report.Rows.Should().Contain(r => r.SessionId == "cs_recorded" && r.Status == StripeReconciliationStatus.Recorded);
        report.Rows.Should().Contain(r => r.SessionId == "cs_missing" && r.Status == StripeReconciliationStatus.Missing);
        report.Rows.Should().Contain(r => r.SessionId == "cs_team" && r.Status == StripeReconciliationStatus.Unmatched);
        report.Rows.Should().Contain(r => r.SessionId == "cs_nometa" && r.Status == StripeReconciliationStatus.Unmatched);
        report.Rows.Should().Contain(r => r.SessionId == "cs_unpaid" && r.Status == StripeReconciliationStatus.Unpaid);
        report.MissingCount.Should().Be(1);
        report.MissingTotalEur.Should().Be(1800m);
    }

    [HumansFact]
    public async Task GetStripeReconciliationAsync_flags_orphan_recorded_payment_absent_from_stripe()
    {
        var order = Guid.NewGuid();
        _stripe.ListStoreCheckoutSessionsAsync(Arg.Any<CancellationToken>()).Returns(new List<StoreCheckoutSessionData>());
        _repo.GetRecordedStripePaymentsAsync(Arg.Any<CancellationToken>()).Returns(new List<StoreRecordedStripePayment>
        {
            new("pi_orphan", order, 500m, Instant.FromUtc(2026, 5, 1, 0, 0)),
        });
        _repo.GetOrderWithLinesAndPaymentsAsync(order, Arg.Any<CancellationToken>()).Returns(CampOrder(order));

        var report = await _service.GetStripeReconciliationAsync();

        report.Orphans.Should().ContainSingle()
            .Which.Should().Match<StripeOrphanPayment>(o => o.PaymentIntentId == "pi_orphan" && o.AmountEur == 500m);
        report.Rows.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetStripeReconciliationAsync_surfaces_health_flags()
    {
        _stripe.IsStoreWebhookConfigured.Returns(false);
        _stripe.IsStoreCheckoutConfigured.Returns(true);
        _stripe.ListStoreCheckoutSessionsAsync(Arg.Any<CancellationToken>()).Returns(new List<StoreCheckoutSessionData>());
        _repo.GetRecordedStripePaymentsAsync(Arg.Any<CancellationToken>()).Returns(new List<StoreRecordedStripePayment>());

        var report = await _service.GetStripeReconciliationAsync();

        report.WebhookConfigured.Should().BeFalse();
        report.CheckoutConfigured.Should().BeTrue();
        report.StripeQueried.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetStripeReconciliationAsync_suppresses_orphans_when_stripe_unavailable()
    {
        var order = Guid.NewGuid();
        // Stripe could not be queried (key unset or missing read scope) → null, not empty list.
        _stripe.ListStoreCheckoutSessionsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StoreCheckoutSessionData>?)null);
        _repo.GetRecordedStripePaymentsAsync(Arg.Any<CancellationToken>()).Returns(new List<StoreRecordedStripePayment>
        {
            new("pi_x", order, 500m, Instant.FromUtc(2026, 5, 1, 0, 0)),
        });

        var report = await _service.GetStripeReconciliationAsync();

        report.StripeQueried.Should().BeFalse();
        report.Orphans.Should().BeEmpty(); // recorded payment is NOT false-flagged as an orphan
        report.Rows.Should().BeEmpty();
    }

    [HumansFact]
    public async Task RecordMissingStripePaymentsAsync_records_only_paid_matched_unrecorded()
    {
        var missingOrder = Guid.NewGuid();
        var recordedOrder = Guid.NewGuid();
        var teamOrder = Guid.NewGuid();
        var actor = Guid.NewGuid();

        _stripe.ListStoreCheckoutSessionsAsync(Arg.Any<CancellationToken>()).Returns(new List<StoreCheckoutSessionData>
        {
            Session("cs_missing", missingOrder, "pi_missing", 1800m, "paid"),
            Session("cs_recorded", recordedOrder, "pi_recorded", 100m, "paid"),  // already recorded → skip
            Session("cs_team", teamOrder, "pi_team", 50m, "paid"),               // non-billable → skip
            Session("cs_unpaid", missingOrder, "pi_unpaid", 200m, "unpaid"),     // not paid → skip
        });
        _repo.GetRecordedStripePaymentsAsync(Arg.Any<CancellationToken>()).Returns(new List<StoreRecordedStripePayment>
        {
            new("pi_recorded", recordedOrder, 100m, Instant.FromUtc(2026, 5, 1, 0, 0)),
        });
        _repo.GetOrderByIdAsync(missingOrder, Arg.Any<CancellationToken>()).Returns(CampOrder(missingOrder));
        _repo.GetOrderByIdAsync(teamOrder, Arg.Any<CancellationToken>()).Returns(TeamOrder(teamOrder));

        var result = await _service.RecordMissingStripePaymentsAsync(actor);

        result.RecordedCount.Should().Be(1);
        result.TotalEur.Should().Be(1800m);
        await _repo.Received(1).AddPaymentAsync(
            Arg.Is<StorePayment>(p =>
                p.OrderId == missingOrder &&
                p.StripePaymentIntentId == "pi_missing" &&
                p.AmountEur == 1800m &&
                p.Method == StorePaymentMethod.Stripe),
            Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().AddPaymentAsync(
            Arg.Is<StorePayment>(p => p.StripePaymentIntentId == "pi_team" || p.StripePaymentIntentId == "pi_unpaid" || p.StripePaymentIntentId == "pi_recorded"),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.StorePaymentsReconciled, "Store", Guid.Empty,
            Arg.Any<string>(), actor, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task RecordMissingStripePaymentsAsync_skips_unmatched_order_and_writes_no_audit()
    {
        var ghostOrder = Guid.NewGuid();
        _stripe.ListStoreCheckoutSessionsAsync(Arg.Any<CancellationToken>()).Returns(new List<StoreCheckoutSessionData>
        {
            Session("cs_ghost", ghostOrder, "pi_ghost", 300m, "paid"),
        });
        _repo.GetRecordedStripePaymentsAsync(Arg.Any<CancellationToken>()).Returns(new List<StoreRecordedStripePayment>());
        _repo.GetOrderByIdAsync(ghostOrder, Arg.Any<CancellationToken>()).Returns((StoreOrder?)null); // deleted/unknown

        var result = await _service.RecordMissingStripePaymentsAsync(Guid.NewGuid());

        result.RecordedCount.Should().Be(0);
        await _repo.DidNotReceive().AddPaymentAsync(Arg.Any<StorePayment>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().LogAsync(
            AuditAction.StorePaymentsReconciled, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }
}

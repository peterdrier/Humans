using AwesomeAssertions;
using Humans.Store.Contracts;
using Humans.Store.Data;
using Humans.Store.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Store.Tests.Data;

public sealed class RepositoryTests
{
    private readonly Repository _repo;

    public RepositoryTests()
    {
        var options = new DbContextOptionsBuilder<StoreDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _repo = new Repository(new TestDbContextFactory<StoreDbContext>(options));
    }

    [HumansFact]
    public async Task AddProductAsync_then_GetActiveProductsForYearAsync_round_trip()
    {
        var product = new Product
        {
            Id = Guid.NewGuid(),
            Year = 2026,
            Name = "Tent rental",
            Description = "Rental of a 4-person tent for the event",
            UnitPriceEur = 50m,
            VatRatePercent = 21m,
            DepositAmountEur = 100m,
            OrderableUntil = new LocalDate(2026, 6, 1),
            IsActive = true,
            CreatedAt = Instant.FromUtc(2026, 3, 1, 12, 0),
            UpdatedAt = Instant.FromUtc(2026, 3, 1, 12, 0)
        };

        await _repo.AddProductAsync(product, Xunit.TestContext.Current.CancellationToken);

        var fetched = await _repo.GetActiveProductsForYearAsync(2026, Xunit.TestContext.Current.CancellationToken);
        fetched.Should().HaveCount(1);
        fetched[0].Id.Should().Be(product.Id);
        fetched[0].Name.Should().Be("Tent rental");
        fetched[0].UnitPriceEur.Should().Be(50m);
        fetched[0].DepositAmountEur.Should().Be(100m);
    }

    [HumansFact]
    public async Task GetActiveProductsForYearAsync_filters_by_year_and_active_flag()
    {
        await _repo.AddProductAsync(MakeProduct(year: 2026, isActive: true, name: "A"), Xunit.TestContext.Current.CancellationToken);
        await _repo.AddProductAsync(MakeProduct(year: 2026, isActive: false, name: "B"), Xunit.TestContext.Current.CancellationToken);
        await _repo.AddProductAsync(MakeProduct(year: 2025, isActive: true, name: "C"), Xunit.TestContext.Current.CancellationToken);

        var results = await _repo.GetActiveProductsForYearAsync(2026, Xunit.TestContext.Current.CancellationToken);

        results.Should().HaveCount(1);
        results[0].Name.Should().Be("A");
    }

    [HumansFact]
    public async Task AddOrderAsync_then_GetOrderWithLinesAndPaymentsAsync_round_trip()
    {
        var orderId = Guid.NewGuid();
        var order = new Order
        {
            Id = orderId,
            CampSeasonId = Guid.NewGuid(),
            CreatedAt = Instant.FromUtc(2026, 3, 1, 12, 0),
            UpdatedAt = Instant.FromUtc(2026, 3, 1, 12, 0)
        };

        await _repo.AddOrderAsync(order, Xunit.TestContext.Current.CancellationToken);

        var fetched = await _repo.GetOrderWithLinesAndPaymentsAsync(orderId, Xunit.TestContext.Current.CancellationToken);
        fetched.Should().NotBeNull();
        fetched.Id.Should().Be(orderId);
        fetched.Lines.Should().BeEmpty();
        fetched.Payments.Should().BeEmpty();
    }

    [HumansFact]
    public async Task SaveIssuedInvoiceAsync_does_not_write_back_a_payment_settled_mid_issuance()
    {
        // The order is read before several slow Holded calls; a Stripe webhook can settle a payment
        // in between. Writing the stale aggregate back would lose the settlement, and there is no
        // concurrency token to catch it.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var at = Instant.FromUtc(2026, 3, 1, 12, 0);
        await _repo.AddOrderAsync(new Order
        {
            Id = orderId,
            CampSeasonId = Guid.NewGuid(),
            Year = 2026,
            State = OrderState.Open,
            CreatedAt = at,
            UpdatedAt = at,
        }, ct);
        await _repo.AddLineAsync(new OrderLine
        {
            Id = lineId,
            OrderId = orderId,
            ProductId = Guid.NewGuid(),
            Qty = 2,
            UnitPriceSnapshot = 4m,
            VatRateSnapshot = 21m,
            AddedAt = at,
            AddedByUserId = Guid.NewGuid(),
        }, ct);
        await _repo.AddPaymentAsync(new Payment
        {
            Id = paymentId,
            OrderId = orderId,
            AmountEur = 10m,
            Method = PaymentMethod.Stripe,
            Status = PaymentStatus.Pending,
            StripePaymentIntentId = "pi_1",
            ReceivedAt = at,
        }, ct);

        // Issuance reads the order — with the payment still Pending.
        var stale = await _repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct);
        stale.Should().NotBeNull();

        // …and while Holded is being called, the webhook settles it.
        await _repo.UpdatePaymentStatusAsync(paymentId, PaymentStatus.Paid, ct);

        stale!.State = OrderState.InvoiceIssued;
        stale.Lines.Single().UnitPriceSnapshot = 5m;
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            HoldedDocId = "doc-1",
            HoldedDocNumber = "2026-0007",
            IssuedAt = at,
            IssuedByUserId = Guid.NewGuid(),
        };
        stale.IssuedInvoiceId = invoice.Id;
        await _repo.SaveIssuedInvoiceAsync(invoice, stale, ct);

        var reloaded = await _repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct);
        reloaded!.Payments.Single().Status.Should().Be(PaymentStatus.Paid);
        // The columns issuance does own still land.
        reloaded.State.Should().Be(OrderState.InvoiceIssued);
        reloaded.IssuedInvoiceId.Should().Be(invoice.Id);
        reloaded.Lines.Single().UnitPriceSnapshot.Should().Be(5m);
    }

    private static Product MakeProduct(int year, bool isActive, string name)
    {
        return new Product
        {
            Id = Guid.NewGuid(),
            Year = year,
            Name = name,
            Description = "desc",
            UnitPriceEur = 10m,
            VatRatePercent = 21m,
            OrderableUntil = new LocalDate(year, 12, 31),
            IsActive = isActive,
            CreatedAt = Instant.FromUtc(year, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(year, 1, 1, 0, 0)
        };
    }
}

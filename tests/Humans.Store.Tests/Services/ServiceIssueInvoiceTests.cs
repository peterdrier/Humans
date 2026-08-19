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
using Xunit;

namespace Humans.Store.Tests.Services;

/// <summary>
/// Phase 5 issuance (nobodies-collective/Humans#1029): the v2 create+approve pipeline, per-line
/// revenue accounts, tax-0 deposit lines to the fianzas account, the receipt/factura branch, and
/// the idempotent re-issue guard.
/// </summary>
public class ServiceIssueInvoiceTests
{
    private const int IceAccountNum = 75_900_002;
    private const int DepositAccountNum = 56_000_000;
    private const string IceAccountId = "acct-ice";
    private const string DepositAccountId = "acct-deposit";

    private readonly IStoreRepository _repo = Substitute.For<IStoreRepository>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly ICampServiceRead _camps = Substitute.For<ICampServiceRead>();
    private readonly ITeamServiceRead _teams = Substitute.For<ITeamServiceRead>();
    private readonly IBurnSettingsService _shifts = Substitute.For<IBurnSettingsService>();
    private readonly IStripeService _stripe = Substitute.For<IStripeService>();
    private readonly IHoldedClient _holded = Substitute.For<IHoldedClient>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 9, 1, 10, 0));
    private readonly StoreSectionOptions _options = new()
    {
        DepositLiabilityAccountNum = DepositAccountNum,
        SimplifiedInvoiceThresholdEur = 400m,
    };
    private readonly Service _service;

    private readonly Guid _productId = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();

    public ServiceIssueInvoiceTests()
    {
        _shifts.GetActiveAsync().Returns(BurnFixtures.Burn(year: 2026, timeZoneId: "Europe/Madrid"));
        _camps.GetCampsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>());
        _holded.IsConfigured.Returns(true);
        _holded.ListAccountingAccountsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            Account(IceAccountNum, IceAccountId),
            Account(DepositAccountNum, DepositAccountId),
        ]);
        _holded.CreateSalesDocumentAsync(
                Arg.Any<HoldedSalesDocumentKind>(), Arg.Any<HoldedSalesDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("doc-1");
        _holded.GetSalesDocumentAsync(
                Arg.Any<HoldedSalesDocumentKind>(), "doc-1", Arg.Any<CancellationToken>())
            .Returns(new HoldedSalesDocumentDto
            {
                Id = "doc-1",
                DocNumber = "2026-0007",
                Subtotal = 100m,
                Tax = 21m,
                Total = 121m,
                Status = "pending",
                RawJson = """{"id":"doc-1"}""",
            });
        _holded.UpsertContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>())
            .Returns("contact-1");

        _service = new Service(
            _repo, _audit, _camps, _teams, _clock, _shifts, _stripe, _holded,
            Options.Create(_options), NullLogger<Service>.Instance);
    }

    private static HoldedAccountDto Account(int number, string id) => new()
    {
        Id = id,
        Number = number,
        Name = $"Account {number}",
        Debit = 0m,
        Credit = 0m,
        Balance = 0m,
    };

    private Product IceProduct(decimal unitPrice = 5m, decimal vat = 21m, decimal? deposit = null, int? account = IceAccountNum) => new()
    {
        Id = _productId,
        Year = 2026,
        Name = "Ice",
        UnitPriceEur = unitPrice,
        VatRatePercent = vat,
        DepositAmountEur = deposit,
        HoldedRevenueAccountNum = account,
        OrderableUntil = new LocalDate(2026, 8, 1),
        IsActive = true,
    };

    /// <summary>An Open camp order for <paramref name="qty"/> units, with counterparty details unless suppressed.</summary>
    private Order CampOrder(int qty = 20, bool identified = true, decimal? depositSnapshot = null)
    {
        var id = Guid.NewGuid();
        return new Order
        {
            Id = id,
            CampSeasonId = Guid.NewGuid(),
            Year = 2026,
            State = OrderState.Open,
            CounterpartyName = identified ? "Camp Frío SL" : null,
            CounterpartyVatId = identified ? "B12345678" : null,
            CounterpartyAddress = identified ? "Calle Falsa 1, Zaragoza" : null,
            CounterpartyCountryCode = identified ? "ES" : null,
            CounterpartyEmail = identified ? "lead@campfrio.example" : null,
            Lines =
            [
                new OrderLine
                {
                    Id = Guid.NewGuid(),
                    OrderId = id,
                    ProductId = _productId,
                    Qty = qty,
                    UnitPriceSnapshot = 4m,
                    VatRateSnapshot = 21m,
                    DepositAmountSnapshot = depositSnapshot,
                },
            ],
        };
    }

    private void Arrange(Order order, Product product)
    {
        _repo.GetOrderWithLinesAndPaymentsAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);
        _repo.GetAllProductsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Product> { product });
        _repo.GetProductsByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Product> { product });
    }

    private async Task<HoldedSalesDocumentInput> CapturedInputAsync(Order order)
    {
        HoldedSalesDocumentInput? captured = null;
        await _holded.CreateSalesDocumentAsync(
            Arg.Any<HoldedSalesDocumentKind>(),
            Arg.Do<HoldedSalesDocumentInput>(i => captured = i),
            Arg.Any<CancellationToken>());
        await _service.IssueInvoiceAsync(order.Id, _actor, TestContext.Current.CancellationToken);
        captured.Should().NotBeNull();
        return captured!;
    }

    [HumansFact]
    public async Task Issues_a_full_factura_with_per_line_revenue_accounts_and_approves_it()
    {
        var order = CampOrder();
        Arrange(order, IceProduct());

        var input = await CapturedInputAsync(order);

        input.ContactId.Should().Be("contact-1");
        input.Lines.Should().HaveCount(1);
        input.Lines[0].AccountId.Should().Be(IceAccountId);
        input.Lines[0].Taxes.Should().Equal("s_iva_21");
        input.Lines[0].Units.Should().Be(20m);
        // Priced from the live catalog, not the stale €4 snapshot — the #816 freeze seam.
        input.Lines[0].Price.Should().Be(5m);

        await _holded.Received(1).CreateSalesDocumentAsync(
            HoldedSalesDocumentKind.Invoice, Arg.Any<HoldedSalesDocumentInput>(), Arg.Any<CancellationToken>());
        // A draft books no revenue: approval is part of issuance.
        await _holded.Received(1).ApproveSalesDocumentAsync(
            HoldedSalesDocumentKind.Invoice, "doc-1", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Writes_the_invoice_row_with_both_payloads_and_freezes_the_order()
    {
        var order = CampOrder();
        Arrange(order, IceProduct());
        Invoice? savedInvoice = null;
        Order? savedOrder = null;
        await _repo.SaveIssuedInvoiceAsync(
            Arg.Do<Invoice>(i => savedInvoice = i),
            Arg.Do<Order>(o => savedOrder = o),
            Arg.Any<CancellationToken>());

        await _service.IssueInvoiceAsync(order.Id, _actor, TestContext.Current.CancellationToken);

        savedInvoice.Should().NotBeNull();
        savedInvoice!.HoldedDocId.Should().Be("doc-1");
        savedInvoice.HoldedDocNumber.Should().Be("2026-0007");
        savedInvoice.IssuedByUserId.Should().Be(_actor);
        savedInvoice.RequestPayload.Should().Contain(IceAccountId);
        savedInvoice.ResponsePayload.Should().Be("""{"id":"doc-1"}""");

        savedOrder.Should().NotBeNull();
        savedOrder!.State.Should().Be(OrderState.InvoiceIssued);
        savedOrder.IssuedInvoiceId.Should().Be(savedInvoice.Id);
        // Snapshots repriced to the live catalog before the freeze.
        savedOrder.Lines.Single().UnitPriceSnapshot.Should().Be(5m);
    }

    [HumansFact]
    public async Task Deposit_lines_post_tax_free_to_the_liability_account()
    {
        var order = CampOrder(qty: 2, depositSnapshot: 30m);
        Arrange(order, IceProduct(unitPrice: 5m, deposit: 30m));

        var input = await CapturedInputAsync(order);

        input.Lines.Should().HaveCount(2);
        var deposit = input.Lines[1];
        deposit.AccountId.Should().Be(DepositAccountId);
        deposit.Taxes.Should().Equal("s_iva_0");
        deposit.Units.Should().Be(2m);
        deposit.Price.Should().Be(30m);
        // The goods line keeps its own rate and its own account.
        input.Lines[0].AccountId.Should().Be(IceAccountId);
        input.Lines[0].Taxes.Should().Equal("s_iva_21");
    }

    [HumansFact]
    public async Task Deposit_without_a_configured_liability_account_is_refused_before_calling_holded()
    {
        _options.DepositLiabilityAccountNum = null;
        var order = CampOrder(qty: 1, depositSnapshot: 30m);
        Arrange(order, IceProduct(deposit: 30m));

        var act = () => _service.IssueInvoiceAsync(order.Id, _actor, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*deposit liability account*");
        await _holded.DidNotReceive().CreateSalesDocumentAsync(
            Arg.Any<HoldedSalesDocumentKind>(), Arg.Any<HoldedSalesDocumentInput>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Order_without_counterparty_under_the_threshold_issues_a_sales_receipt()
    {
        // 20 × €5 + 21% = €121, under the €400 simplified-invoice threshold.
        var order = CampOrder(identified: false);
        Arrange(order, IceProduct());

        await _service.IssueInvoiceAsync(order.Id, _actor, TestContext.Current.CancellationToken);

        await _holded.Received(1).CreateSalesDocumentAsync(
            HoldedSalesDocumentKind.SalesReceipt, Arg.Any<HoldedSalesDocumentInput>(), Arg.Any<CancellationToken>());
        await _holded.Received(1).ApproveSalesDocumentAsync(
            HoldedSalesDocumentKind.SalesReceipt, "doc-1", Arg.Any<CancellationToken>());
        // A receipt has no counterparty, so no contact is created for it.
        await _holded.DidNotReceive().UpsertContactAsync(
            Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Order_without_counterparty_over_the_threshold_is_refused()
    {
        // 200 × €5 + 21% = €1,210 — a receipt is not lawful at that amount.
        var order = CampOrder(qty: 200, identified: false);
        Arrange(order, IceProduct());

        var act = () => _service.IssueInvoiceAsync(order.Id, _actor, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*full factura*");
        await _holded.DidNotReceive().CreateSalesDocumentAsync(
            Arg.Any<HoldedSalesDocumentKind>(), Arg.Any<HoldedSalesDocumentInput>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Re_issuing_an_issued_order_throws_without_calling_holded()
    {
        var order = CampOrder();
        order.State = OrderState.InvoiceIssued;
        order.IssuedInvoiceId = Guid.NewGuid();
        Arrange(order, IceProduct());

        var act = () => _service.IssueInvoiceAsync(order.Id, _actor, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already been invoiced*");
        await _holded.DidNotReceive().CreateSalesDocumentAsync(
            Arg.Any<HoldedSalesDocumentKind>(), Arg.Any<HoldedSalesDocumentInput>(), Arg.Any<CancellationToken>());
        await _holded.DidNotReceive().ListAccountingAccountsAsync(Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().SaveIssuedInvoiceAsync(
            Arg.Any<Invoice>(), Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Product_without_a_revenue_account_is_refused_by_name()
    {
        var order = CampOrder();
        Arrange(order, IceProduct(account: null));

        var act = () => _service.IssueInvoiceAsync(order.Id, _actor, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Ice*");
        await _holded.DidNotReceive().CreateSalesDocumentAsync(
            Arg.Any<HoldedSalesDocumentKind>(), Arg.Any<HoldedSalesDocumentInput>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Account_missing_from_the_holded_chart_is_refused()
    {
        _holded.ListAccountingAccountsAsync(Arg.Any<CancellationToken>())
            .Returns([Account(DepositAccountNum, DepositAccountId)]);
        var order = CampOrder();
        Arrange(order, IceProduct());

        var act = () => _service.IssueInvoiceAsync(order.Id, _actor, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{IceAccountNum}*");
        await _holded.DidNotReceive().CreateSalesDocumentAsync(
            Arg.Any<HoldedSalesDocumentKind>(), Arg.Any<HoldedSalesDocumentInput>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Team_orders_are_never_invoiced()
    {
        var order = CampOrder();
        order.CampSeasonId = null;
        order.TeamId = Guid.NewGuid();
        Arrange(order, IceProduct());

        var act = () => _service.IssueInvoiceAsync(order.Id, _actor, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*non-billable*");
    }

    [HumansFact]
    public async Task Unconfigured_holded_refuses_issuance_rather_than_silently_freezing_the_order()
    {
        _holded.IsConfigured.Returns(false);
        var order = CampOrder();
        Arrange(order, IceProduct());

        var act = () => _service.IssueInvoiceAsync(order.Id, _actor, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
        await _repo.DidNotReceive().SaveIssuedInvoiceAsync(
            Arg.Any<Invoice>(), Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(21, "s_iva_21")]
    [InlineData(10, "s_iva_10")]
    [InlineData(4, "s_iva_4")]
    [InlineData(0, "s_iva_0")]
    public async Task Vat_rate_maps_to_the_holded_sales_tax_key(int vatRate, string expectedKey)
    {
        var order = CampOrder(qty: 1);
        Arrange(order, IceProduct(vat: vatRate));

        var input = await CapturedInputAsync(order);

        input.Lines[0].Taxes.Should().Equal(expectedKey);
    }
}

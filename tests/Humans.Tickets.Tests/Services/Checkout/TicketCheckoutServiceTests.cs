using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Grants.Contracts;
using Humans.Settings.Contracts;
using Humans.Testing;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using Humans.Users.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Tickets.Tests.Services.Checkout;

public sealed class TicketCheckoutServiceTests
{
    private static readonly Instant Now = Instant.FromUtc(2027, 1, 10, 12, 0);
    private static readonly Guid LowIncomeProgram = Guid.NewGuid();
    private static readonly Guid CreditProgram = Guid.NewGuid();

    private readonly FakeCheckoutRepository _repo = new();
    private readonly ITicketCheckoutBackend _backend = Substitute.For<ITicketCheckoutBackend>();
    private readonly IGrantService _grants = Substitute.For<IGrantService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IAccountProvisioningService _provisioning = Substitute.For<IAccountProvisioningService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly Guid _buyerId = Guid.NewGuid();
    private readonly EventSettingsInfo _event;
    private readonly TicketSalesEvent _salesEvent;
    private readonly TicketCheckoutService _service;

    public TicketCheckoutServiceTests()
    {
        _event = new EventSettingsInfo(Guid.NewGuid(), "Nowhere 2027", 2027, "Europe/Madrid", new LocalDate(2027, 6, 1),
            -7, 5, 8, new Dictionary<int, int>(), null, null);
        _settings.GetActiveEventSettings().Returns(_event);
        _settings.GetEventSettingsByIdAsync(_event.Id, Arg.Any<CancellationToken>()).Returns(_event);

        _salesEvent = new TicketSalesEvent
        {
            Id = Guid.NewGuid(),
            EventSettingsId = _event.Id,
            BackendKey = "test",
            Mode = TicketSalesMode.Open,
            Capacity = 3000,
            TicketVatRate = 0.10m,
            Waves =
            [
                new TicketSalesWave { Name = "Wave 1", EndsOn = new LocalDate(2027, 1, 31), PriceMinor = 22500 },
                new TicketSalesWave { Name = "Wave 2", EndsOn = new LocalDate(2027, 2, 28), PriceMinor = 25000 },
            ],
            SupporterTotalsMinor = [40000, 100000],
            PriceRules =
            [
                new TicketPriceRule { GrantProgramId = LowIncomeProgram, Kind = TicketPriceRuleKind.ClassPrice, AmountMinor = 12500, Label = "Low income" },
                new TicketPriceRule { GrantProgramId = CreditProgram, Kind = TicketPriceRuleKind.Credit, AmountMinor = 5000, Label = "Credit" },
            ],
        };
        _repo.SalesEvents.Add(_salesEvent);

        _backend.Key.Returns("test");
        _backend.IsConfigured.Returns(true);
        _backend.CreateCheckoutAsync(Arg.Any<CheckoutRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CheckoutSession("cs_1", new Uri("https://pay.example/cs_1")));
        _grants.GetActiveGrantsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);

        var buyer = new User { Id = _buyerId, BurnerName = "Sparky", FirstName = "Ana", LastName = "García", Email = "ana@example.com" };
        _users.GetUserInfoAsync(_buyerId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(buyer, [], [], UserFixtures.Profile()));

        _service = new TicketCheckoutService(_repo, [_backend], _grants, _settings, _users, _provisioning, _audit,
            new FakeClock(Now), NullLogger<TicketCheckoutService>.Instance);
    }

    private static StartCheckout Buy(
        bool forSelf = true, string? partnerName = null, string? partnerEmail = null, long? supporter = null, long donation = 0) =>
        new(forSelf, partnerName, partnerEmail, supporter, donation, TicketDonationPurpose.General,
            new Uri("https://humans.example/Tickets/Buy/Order/"), "es-ES");

    [HumansFact]
    public void Price_is_the_wave_on_the_pricing_date_then_the_last_wave()
    {
        TicketPrice.For(_salesEvent, new LocalDate(2027, 1, 31), []).Should().NotBeNull()
            .And.Subject.As<TicketPrice>().AmountMinor.Should().Be(22500);
        TicketPrice.For(_salesEvent, new LocalDate(2027, 2, 1), [])!.AmountMinor.Should().Be(25000);
        TicketPrice.For(_salesEvent, new LocalDate(2027, 5, 1), [])!.Wave.Name.Should().Be("Wave 2");
    }

    [HumansFact]
    public void Class_price_replaces_the_wave_and_credits_stack_on_it_never_below_zero()
    {
        var price = TicketPrice.For(_salesEvent, new LocalDate(2027, 2, 10), [LowIncomeProgram, CreditProgram])!;
        price.ClassPrice!.Label.Should().Be("Low income");
        price.AmountMinor.Should().Be(7500);

        _salesEvent.PriceRules.Add(new TicketPriceRule { GrantProgramId = CreditProgram, Kind = TicketPriceRuleKind.Credit, AmountMinor = 20000 });
        TicketPrice.For(_salesEvent, new LocalDate(2027, 2, 10), [LowIncomeProgram, CreditProgram])!.AmountMinor.Should().Be(0);
    }

    [HumansFact]
    public async Task Supporter_total_is_the_ticket_at_the_buyers_price_plus_a_donation_line_without_VAT()
    {
        var result = await _service.StartCheckoutAsync(Buy(supporter: 100000), _buyerId, TestContext.Current.CancellationToken);

        result.Refusal.Should().BeNull();
        result.PaymentUrl.Should().Be(new Uri("https://pay.example/cs_1"));
        var order = _repo.Orders.Single();
        order.State.Should().Be(TicketCheckoutOrderState.Pending);
        order.BackendOrderId.Should().Be("cs_1");
        order.TotalMinor.Should().Be(100000);
        await _backend.Received(1).CreateCheckoutAsync(
            Arg.Is<CheckoutRequest>(r =>
                r.OrderId == order.Id
                && r.BuyerName == "Ana García"
                && r.Lines.Count == 2
                && r.Lines[0].Kind == CheckoutLineKind.Ticket && r.Lines[0].AmountMinor == 22500 && r.Lines[0].VatRate == 0.10m
                && r.Lines[0].AttendeeName == "Ana García"
                && r.Lines[1].Kind == CheckoutLineKind.Donation && r.Lines[1].AmountMinor == 77500 && r.Lines[1].VatRate == 0m),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Partner_gets_an_account_and_pays_the_public_price_even_when_the_buyer_has_a_class_price()
    {
        _grants.GetActiveGrantsAsync(_buyerId, Arg.Any<CancellationToken>())
            .Returns([new ActiveGrantInfo(Guid.NewGuid(), LowIncomeProgram, "Low income", _event.Id)]);
        var partnerId = Guid.NewGuid();
        _provisioning.FindOrCreateUserByEmailAsync("bo@example.com", "Bo Smith", ContactSource.Manual, Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(partnerId, Created: true));

        var result = await _service.StartCheckoutAsync(
            Buy(partnerName: "Bo Smith", partnerEmail: "bo@example.com"), _buyerId, TestContext.Current.CancellationToken);

        result.Refusal.Should().BeNull();
        var lines = _repo.Orders.Single().Lines;
        lines.Should().HaveCount(2);
        lines[0].AmountMinor.Should().Be(12500);
        lines[1].AmountMinor.Should().Be(22500);
        lines[1].AttendeeUserId.Should().Be(partnerId);
    }

    [HumansFact]
    public async Task Sold_out_and_resale_only_refuse_fresh_tickets_but_not_a_donation()
    {
        _salesEvent.Capacity = 0;
        (await _service.StartCheckoutAsync(Buy(), _buyerId, TestContext.Current.CancellationToken))
            .Refusal.Should().Be("Tickets_Buy_SoldOut");

        _salesEvent.Capacity = 3000;
        _salesEvent.Mode = TicketSalesMode.ResaleOnly;
        (await _service.StartCheckoutAsync(Buy(), _buyerId, TestContext.Current.CancellationToken))
            .Refusal.Should().Be("Tickets_Buy_ResaleOnly");
        (await _service.StartCheckoutAsync(Buy(forSelf: false, donation: 5000), _buyerId, TestContext.Current.CancellationToken))
            .Refusal.Should().BeNull();
    }

    [HumansFact]
    public async Task Backend_failure_marks_the_order_failed_so_it_holds_no_capacity()
    {
        _backend.CreateCheckoutAsync(Arg.Any<CheckoutRequest>(), Arg.Any<CancellationToken>())
            .Returns<CheckoutSession>(_ => throw new TicketCheckoutException("nope"));

        var result = await _service.StartCheckoutAsync(Buy(), _buyerId, TestContext.Current.CancellationToken);

        result.Refusal.Should().Be("Tickets_Buy_BackendFailed");
        _repo.Orders.Single().State.Should().Be(TicketCheckoutOrderState.Failed);
    }

    [HumansFact]
    public async Task Webhook_nudge_reads_the_backend_status_and_issues_a_code_per_ticket()
    {
        await _service.StartCheckoutAsync(Buy(), _buyerId, TestContext.Current.CancellationToken);
        _backend.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new CheckoutStatus(CheckoutState.Paid, 455, "ideal", Now));

        await _service.CheckoutChangedAsync("test", "cs_1", TestContext.Current.CancellationToken);
        await _service.CheckoutChangedAsync("test", "cs_1", TestContext.Current.CancellationToken);

        var order = _repo.Orders.Single();
        order.State.Should().Be(TicketCheckoutOrderState.Paid);
        order.FeeMinor.Should().Be(455);
        order.PaymentMethod.Should().Be("ideal");
        order.Lines.Single().Barcode.Should().MatchRegex("^H[0-9A-Z]{16}$");
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCheckoutPaid, Arg.Any<string>(), order.Id,
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task Refund_is_capped_at_the_ticket_lines_net_of_earlier_refunds_and_never_touches_donations()
    {
        await _service.StartCheckoutAsync(Buy(supporter: 40000), _buyerId, TestContext.Current.CancellationToken);
        _backend.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new CheckoutStatus(CheckoutState.Paid, 455, "card", Now));
        await _service.CheckoutChangedAsync("test", "cs_1", TestContext.Current.CancellationToken);
        var order = _repo.Orders.Single();
        _backend.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Succeeded, "re_1", null));
        var actor = Guid.NewGuid();

        (await _service.RefundAsync(order.Id, 22501, "too much", actor, TestContext.Current.CancellationToken))
            .Succeeded.Should().BeFalse();
        (await _service.RefundAsync(order.Id, 10000, "partial", actor, TestContext.Current.CancellationToken))
            .Succeeded.Should().BeTrue();
        (await _service.RefundAsync(order.Id, 12501, "rest plus one", actor, TestContext.Current.CancellationToken))
            .Succeeded.Should().BeFalse();
        (await _service.RefundAsync(order.Id, 12500, "rest", actor, TestContext.Current.CancellationToken))
            .Succeeded.Should().BeTrue();

        await _backend.Received(2).RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
        await _backend.Received(1).RefundAsync(
            Arg.Is<RefundRequest>(r => r.BackendOrderId == "cs_1" && r.AmountMinor == 10000), Arg.Any<CancellationToken>());
        _repo.Refunds.Select(r => r.RefundId).Should().OnlyHaveUniqueItems();
    }

    [HumansFact]
    public async Task Refund_of_an_unpaid_order_is_refused_without_calling_the_backend()
    {
        await _service.StartCheckoutAsync(Buy(), _buyerId, TestContext.Current.CancellationToken);

        (await _service.RefundAsync(_repo.Orders.Single().Id, 100, "nope", Guid.NewGuid(), TestContext.Current.CancellationToken))
            .Succeeded.Should().BeFalse();
        await _backend.DidNotReceive().RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
    }

    private sealed class FakeCheckoutRepository : ITicketCheckoutRepository
    {
        public List<TicketSalesEvent> SalesEvents { get; } = [];
        public List<TicketCheckoutOrder> Orders { get; } = [];
        public List<(Guid RefundId, TicketCheckoutRefund Refund)> Refunds { get; } = [];

        public Task<IReadOnlyList<TicketSalesEvent>> GetSalesEventsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketSalesEvent>>(SalesEvents.ToList());
        public Task<TicketSalesEvent?> GetSalesEventAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(SalesEvents.FirstOrDefault(e => e.Id == id));
        public Task<TicketSalesEvent?> GetSalesEventForEventAsync(Guid eventSettingsId, CancellationToken ct = default) =>
            Task.FromResult(SalesEvents.FirstOrDefault(e => e.EventSettingsId == eventSettingsId));
        public Task SaveSalesEventAsync(TicketSalesEvent salesEvent, CancellationToken ct = default)
        {
            SalesEvents.RemoveAll(e => e.Id == salesEvent.Id);
            SalesEvents.Add(salesEvent);
            return Task.CompletedTask;
        }
        public Task<TicketCheckoutOrder?> GetOrderAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Orders.FirstOrDefault(o => o.Id == id));
        public Task<TicketCheckoutOrder?> GetOrderByBackendIdAsync(string backendKey, string backendOrderId, CancellationToken ct = default) =>
            Task.FromResult(Orders.FirstOrDefault(o => string.Equals(o.BackendKey, backendKey, StringComparison.Ordinal) && string.Equals(o.BackendOrderId, backendOrderId, StringComparison.Ordinal)));
        public Task<IReadOnlyList<TicketCheckoutOrder>> GetOrdersForSalesEventAsync(Guid salesEventId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketCheckoutOrder>>(Orders.Where(o => o.SalesEventId == salesEventId).ToList());
        public Task<IReadOnlyList<TicketCheckoutOrder>> GetOrdersForUserAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketCheckoutOrder>>(
                Orders.Where(o => o.BuyerUserId == userId || o.Lines.Any(l => l.AttendeeUserId == userId)).ToList());
        public Task<IReadOnlyList<TicketCheckoutOrder>> GetOpenOrdersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketCheckoutOrder>>(Orders
                .Where(o => o.State is TicketCheckoutOrderState.Created or TicketCheckoutOrderState.Pending).ToList());
        public Task AddOrderAsync(TicketCheckoutOrder order, CancellationToken ct = default)
        {
            Orders.Add(order);
            return Task.CompletedTask;
        }
        public Task UpdateOrderAsync(TicketCheckoutOrder order, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TicketCheckoutRefund>> GetRefundsAsync(Guid orderId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketCheckoutRefund>>(Refunds.Select(r => r.Refund).Where(r => r.OrderId == orderId).ToList());
        public Task AddRefundAsync(TicketCheckoutRefund refund, CancellationToken ct = default)
        {
            Refunds.Add((refund.Id, refund));
            return Task.CompletedTask;
        }
        public Task UpdateRefundAsync(TicketCheckoutRefund refund, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> ErasePiiForUserAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(0);
        public Task ReassignAsync(Guid fromUserId, Guid toUserId, CancellationToken ct = default) => Task.CompletedTask;
    }
}

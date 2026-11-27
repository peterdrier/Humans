using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Configuration;
using Humans.Email.Contracts;
using Humans.Grants.Contracts;
using Humans.Settings.Contracts;
using Humans.Testing;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Services;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using Humans.Tickets.Services.Stores;
using Microsoft.Extensions.DependencyInjection;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Tickets.Tests.Services.Checkout;

/// <summary>The checkout service over a fake repository and one test backend, shared by the checkout lane's service tests.</summary>
public abstract class CheckoutServiceFixture
{
    private protected static readonly Instant Now = Instant.FromUtc(2027, 1, 10, 12, 0);
    private protected static readonly Guid LowIncomeProgram = Guid.NewGuid();
    private protected static readonly Guid CreditProgram = Guid.NewGuid();

    private protected readonly FakeCheckoutRepository _repo = new();
    private protected readonly ITicketCheckoutBackend _backend = Substitute.For<ITicketCheckoutBackend>();
    private protected readonly IGrantService _grants = Substitute.For<IGrantService>();
    private protected readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private protected readonly IUserService _users = Substitute.For<IUserService>();
    private protected readonly IAccountProvisioningService _provisioning = Substitute.For<IAccountProvisioningService>();
    private protected readonly IUserEmailService _userEmails = Substitute.For<IUserEmailService>();
    private protected readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private protected readonly ICommunicationPreferenceService _preferenceReads = Substitute.For<ICommunicationPreferenceService>();
    private protected readonly ICommunicationPreferenceWrite _preferences = Substitute.For<ICommunicationPreferenceWrite>();
    private protected readonly IEmailService _email = Substitute.For<IEmailService>();
    private protected readonly ITicketDelivery _delivery = Substitute.For<ITicketDelivery>();
    private protected readonly Humans.Gate.Contracts.IGateAdmissions _gate = Substitute.For<Humans.Gate.Contracts.IGateAdmissions>();
    private protected readonly ITicketRulesSource _rules = Substitute.For<ITicketRulesSource>();
    private protected readonly HashSet<string> _admitted = new(StringComparer.Ordinal);
    private protected readonly FakeClock _clock = new(Now);
    private protected readonly ITicketManagement _holdingsInner = Substitute.For<ITicketManagement>();
    private protected readonly CachingTicketQueryService _holdings;
    private protected readonly Guid _otherBuyerId = Guid.NewGuid();
    private protected int _sessions;
    private protected readonly Guid _buyerId = Guid.NewGuid();
    private protected readonly EventSettingsInfo _event;
    private protected readonly TicketSalesEvent _salesEvent;
    private protected readonly TicketCheckoutService _service;

    protected CheckoutServiceFixture()
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
            ResaleOpenedAt = Instant.FromUtc(2026, 12, 1, 0, 0),
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
            .Returns(_ =>
            {
                var id = $"cs_{++_sessions}";
                return new CheckoutSession(id, new Uri($"https://pay.example/{id}"));
            });
        _backend.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Succeeded, "re_1", null));
        _grants.GetActiveGrantsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);
        _userEmails.FindByAddressAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var buyer = new User { Id = _buyerId, BurnerName = "Sparky", FirstName = "Ana", LastName = "García", Email = "ana@example.com" };
        _users.GetUserInfoAsync(_buyerId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(buyer, [], [], UserFixtures.Profile()));
        var other = new User { Id = _otherBuyerId, BurnerName = "Moth", FirstName = "Bo", LastName = "Smith", Email = "bo@example.com" };
        _users.GetUserInfoAsync(_otherBuyerId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(other, [], [], UserFixtures.Profile()));
        var localizer = Substitute.For<IStringLocalizer<TicketsResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        localizer["Tickets_Resale_Email_Invite_Body"].Returns(new LocalizedString("body", "{2}"));
        var emails = new TicketsEmails(Options.Create(new EmailSettings()), localizer, NullLogger<TicketsEmails>.Instance);

        _gate.GetAdmittedAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlySet<string>)ci.Arg<IReadOnlyCollection<string>>().Where(_admitted.Contains).ToHashSet(StringComparer.Ordinal));
        var services = new ServiceCollection();
        services.AddKeyedScoped(CachingTicketQueryService.InnerServiceKey, (_, _) => _holdingsInner);
        _holdings = new CachingTicketQueryService(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            _clock, NullLogger<CachingTicketQueryService>.Instance);
        _service = new TicketCheckoutService(_repo, [_backend], _grants, _settings, _users, _provisioning, _userEmails,
            _preferenceReads, _preferences, _email, emails, [_delivery], _gate, _rules, localizer, _audit,
            _clock, _holdings, NullLogger<TicketCheckoutService>.Instance);
    }

    private protected StartCheckout Buy(
        bool forSelf = true, long? supporter = null, long donation = 0, Guid? salesEventId = null, string?[]? others = null) =>
        new(salesEventId ?? _salesEvent.Id, forSelf, supporter, donation, null,
            new Uri("https://humans.example/Tickets/Buy/Order/"), "es-ES")
        { OtherEmails = others ?? [] };

    /// <summary>A person in Humans who has verified <paramref name="email"/>.</summary>
    private protected Guid Human(string email, string first, string last, Action<User>? shape = null)
    {
        var user = new User { Id = Guid.NewGuid(), BurnerName = first, FirstName = first, LastName = last, Email = email };
        shape?.Invoke(user);
        _users.GetUserInfoAsync(user.Id, Arg.Any<CancellationToken>()).Returns(UserInfo.Create(user, [], [], UserFixtures.Profile()));
        _userEmails.FindByAddressAsync(Arg.Is<string>(e => string.Equals(e, email, StringComparison.OrdinalIgnoreCase)), true, true,
            Arg.Any<CancellationToken>()).Returns([Row(user.Id, verified: true)]);
        return user.Id;
    }

    private protected static UserEmailRowSnapshot Row(Guid userId, bool verified) =>
        new(Guid.NewGuid(), userId, "bo@example.com", verified, null, null, IsPrimary: true, null, null, Now, Now);

    private protected async Task PayAsync(string session, Instant? paidAt = null)
    {
        _backend.GetStatusAsync(session, Arg.Any<CancellationToken>())
            .Returns(new CheckoutStatus(CheckoutState.Paid, 100, "card", paidAt ?? Now));
        await _service.CheckoutChangedAsync("test", session, Ct);
    }

    private protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    private protected async Task<TicketCheckoutOrder> BuyAndPayAsync(StartCheckout command, Guid userId)
    {
        var result = await _service.StartCheckoutAsync(command, userId, Ct);
        result.Refusal.Should().BeNull();
        var order = _repo.Orders.Single(o => o.Id == result.OrderId);
        _backend.GetStatusAsync(order.BackendOrderId!, Arg.Any<CancellationToken>())
            .Returns(new CheckoutStatus(CheckoutState.Paid, 0, "card", _clock.GetCurrentInstant()));
        await _service.CheckoutChangedAsync("test", order.BackendOrderId!, Ct);
        return order;
    }

    private protected async Task<TicketCheckoutTicket> OfferedTicketAsync(StartCheckout? command = null)
    {
        var order = await BuyAndPayAsync(command ?? Buy(), _buyerId);
        var adult = _repo.Tickets.Single(t => t.OrderId == order.Id && t.Kind == TicketHolderKind.Adult);
        (await _service.OfferForResaleAsync(adult.Id, _buyerId, Ct)).Refusal.Should().BeNull();
        _salesEvent.Mode = TicketSalesMode.ResaleOnly;
        return adult;
    }

    /// <summary>The evening batch, run once the new buyer's order has held long enough.</summary>
    private protected async Task<ResaleRefundBatch> RefundBatchAsync()
    {
        _clock.Advance(Duration.FromHours(12));
        return await _service.RunResaleRefundBatchAsync(Ct);
    }

    private protected TicketRules RulesAt(string version) =>
        new("TicketRules/2027", version, new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "# Normas", ["en"] = "# Rules" });

    private protected async Task SaveResaleOffAsync()
    {
        var save = new SalesEventSave(_salesEvent.Id, _event.Id, "test", null, TicketSalesMode.Open, null, 3000, 0.10m,
            _salesEvent.Waves, [], [])
        {
            Switches = new TicketSalesSwitches { Resale = ResaleScheme.Off },
        };
        (await _service.SaveSalesEventAsync(save, _buyerId, Ct)).Refusal.Should().BeNull();
    }

    private protected readonly Guid _adminId = Guid.NewGuid();

    private protected async Task<TicketCheckoutTicket> PaidAdultAsync()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        return _repo.Tickets.Single(t => t.OrderId == order.Id && t.Kind == TicketHolderKind.Adult);
    }

    private protected sealed class FakeCheckoutRepository : ITicketCheckoutRepository
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
                .Where(o => o.IsOpen).ToList());
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

        public List<TicketCheckoutOrderEvent> Events { get; } = [];

        public Task AddOrderEventAsync(TicketCheckoutOrderEvent orderEvent, CancellationToken ct = default)
        {
            Events.Add(orderEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TicketCheckoutOrderEvent>> GetOrderEventsAsync(Guid orderId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketCheckoutOrderEvent>>(Events.Where(e => e.OrderId == orderId).ToList());
        public List<TicketCheckoutTicket> Tickets { get; } = [];
        public List<TicketResaleOffer> Offers { get; } = [];

        public HashSet<Guid> ClaimedPaid { get; } = [];

        public Task<bool> TryClaimPaidAsync(Guid orderId, Instant paidAt, CancellationToken ct = default) =>
            Task.FromResult(ClaimedPaid.Add(orderId));

        public Task<bool> AddTicketsAsync(IReadOnlyList<TicketCheckoutTicket> tickets, CancellationToken ct = default)
        {
            if (tickets.Any(n => Tickets.Any(t => t.OrderId == n.OrderId && t.LineIndex == n.LineIndex)))
                return Task.FromResult(false);
            Tickets.AddRange(tickets);
            return Task.FromResult(true);
        }
        public Task UpdateTicketAsync(TicketCheckoutTicket ticket, CancellationToken ct = default) => Task.CompletedTask;
        public Task<TicketCheckoutTicket?> GetTicketAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Tickets.FirstOrDefault(t => t.Id == id));
        public Task<TicketCheckoutTicket?> GetTicketByBarcodeAsync(string barcode, CancellationToken ct = default) =>
            Task.FromResult(Tickets.FirstOrDefault(t => string.Equals(t.Barcode, barcode, StringComparison.Ordinal)));
        public Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForOrderAsync(Guid orderId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketCheckoutTicket>>(Tickets.Where(t => t.OrderId == orderId).ToList());
        public Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForHolderAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketCheckoutTicket>>(Tickets.Where(t => t.HolderUserId == userId).ToList());
        public Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForSalesEventAsync(Guid salesEventId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketCheckoutTicket>>(Tickets.Where(t => t.SalesEventId == salesEventId).ToList());
        public Task AddOfferAsync(TicketResaleOffer offer, CancellationToken ct = default)
        {
            Offers.Add(offer);
            return Task.CompletedTask;
        }
        public Task UpdateOfferAsync(TicketResaleOffer offer, CancellationToken ct = default) => Task.CompletedTask;
        public Task<TicketResaleOffer?> GetOfferAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Offers.FirstOrDefault(o => o.Id == id));
        public Task<TicketResaleOffer?> GetOfferByTokenHashAsync(string tokenHash, CancellationToken ct = default) =>
            Task.FromResult(Offers.FirstOrDefault(o => o.InviteFor(tokenHash) is not null));
        public Task<IReadOnlyList<TicketResaleOffer>> GetOffersForSalesEventAsync(Guid salesEventId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketResaleOffer>>(Offers.Where(o => o.SalesEventId == salesEventId).ToList());
        public List<TicketCompanionRequest> CompanionRequests { get; } = [];
        public Task AddCompanionRequestAsync(TicketCompanionRequest request, CancellationToken ct = default)
        {
            CompanionRequests.Add(request);
            return Task.CompletedTask;
        }
        public Task UpdateCompanionRequestAsync(TicketCompanionRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task<TicketCompanionRequest?> GetCompanionRequestAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(CompanionRequests.FirstOrDefault(r => r.Id == id));
        public Task<IReadOnlyList<TicketCompanionRequest>> GetCompanionRequestsForSalesEventAsync(Guid salesEventId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketCompanionRequest>>(CompanionRequests.Where(r => r.SalesEventId == salesEventId).ToList());
        public Task<IReadOnlyList<TicketCompanionRequest>> GetCompanionRequestsForUserAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketCompanionRequest>>(
                CompanionRequests.Where(r => r.HolderUserId == userId || r.CompanionUserId == userId).ToList());
        public Task<IReadOnlyList<TicketCheckoutRefund>> GetRefundsForSalesEventAsync(Guid salesEventId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketCheckoutRefund>>(Refunds.Select(r => r.Refund)
                .Where(r => Orders.Any(o => o.Id == r.OrderId && o.SalesEventId == salesEventId)).ToList());
        public int Erased { get; private set; }

        public IReadOnlyCollection<string> ErasedAddresses { get; private set; } = [];

        public Task<int> ErasePiiForUserAsync(Guid userId, IReadOnlyCollection<string> emails, CancellationToken ct = default)
        {
            Erased++;
            ErasedAddresses = emails;
            return Task.FromResult(0);
        }
        public Task ReassignAsync(Guid fromUserId, Guid toUserId, CancellationToken ct = default) => Task.CompletedTask;
    }
}

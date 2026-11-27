using Microsoft.AspNetCore.DataProtection;
using Humans.Testing;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Configuration;
using Humans.Email.Contracts;
using Humans.Grants.Contracts;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Services;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using Humans.Tickets.Services.Stores;
using Microsoft.Extensions.DependencyInjection;
using Humans.Users.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Tickets.Tests.Services.Checkout;

public sealed partial class TicketCheckoutServiceTests
{
    private static readonly Instant Now = Instant.FromUtc(2027, 1, 10, 12, 0);
    private static readonly Guid LowIncomeProgram = Guid.NewGuid();
    private static readonly Guid DiscountProgram = Guid.NewGuid();

    private readonly FakeCheckoutRepository _repo = new();
    private readonly ITicketingPlatform _platform = Substitute.For<ITicketingPlatform>();
    private readonly IGrantService _grants = Substitute.For<IGrantService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IAccountProvisioningService _provisioning = Substitute.For<IAccountProvisioningService>();
    private readonly IUserEmailService _userEmails = Substitute.For<IUserEmailService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly ICommunicationPreferenceService _preferenceReads = Substitute.For<ICommunicationPreferenceService>();
    private readonly ICommunicationPreferenceWrite _preferences = Substitute.For<ICommunicationPreferenceWrite>();
    private readonly IEmailService _email = Substitute.For<IEmailService>();
    private readonly ITicketDelivery _delivery = Substitute.For<ITicketDelivery>();
    private readonly Humans.Gate.Contracts.IGateAdmissions _gate = Substitute.For<Humans.Gate.Contracts.IGateAdmissions>();
    private readonly ITicketRulesSource _rules = Substitute.For<ITicketRulesSource>();
    private readonly HashSet<string> _admitted = new(StringComparer.Ordinal);
    private readonly FakeClock _clock = new(Now);
    private readonly IDataProtectionProvider _resaleProtection = new EphemeralDataProtectionProvider();
    private readonly IHostEnvironment _environment = Substitute.For<IHostEnvironment>();
    private readonly ITicketManagement _holdingsInner = Substitute.For<ITicketManagement>();
    private readonly CachingTicketQueryService _holdings;
    private readonly Guid _otherBuyerId = Guid.NewGuid();
    private int _sessions;
    private readonly Guid _buyerId = Guid.NewGuid();
    private readonly EventSettingsInfo _event;
    private readonly TicketingSettings _ticketing;
    private readonly TicketCheckoutService _service;

    public TicketCheckoutServiceTests()
    {
        _event = new EventSettingsInfo(Guid.NewGuid(), "Nowhere 2027", 2027, "Europe/Madrid", new LocalDate(2027, 6, 1),
            -7, 5, 8, new Dictionary<int, int>(), null, null);
        _settings.GetActiveEventSettings().Returns(_event);
        _settings.GetEventSettingsByIdAsync(_event.Id, Arg.Any<CancellationToken>()).Returns(_event);

        _ticketing = new TicketingSettings
        {
            Id = Guid.NewGuid(),
            EventSettingsId = _event.Id,
            PlatformKey = "test",
            Mode = TicketSalesMode.Open,
            Capacity = 3000,
            TicketVatRate = 0.10m,
            ResaleOpenedAt = Instant.FromUtc(2026, 12, 1, 0, 0),
            Waves =
            [
                new TicketSalesWave { Name = "Wave 1", EndsOn = new LocalDate(2027, 1, 31), PriceMinor = 22500 },
                new TicketSalesWave { Name = "Wave 2", EndsOn = new LocalDate(2027, 2, 28), PriceMinor = 25000 },
            ],
            VipTotalsMinor = [40000, 100000],
            PriceRules =
            [
                new TicketPriceRule { GrantProgramId = LowIncomeProgram, Kind = TicketPriceRuleKind.ClassPrice, AmountMinor = 12500, Label = "Low income" },
                new TicketPriceRule { GrantProgramId = DiscountProgram, Kind = TicketPriceRuleKind.Discount, AmountMinor = 5000, Label = "Discount" },
            ],
        };
        _repo.Settings.Add(_ticketing);

        _environment.EnvironmentName.Returns(Environments.Production);
        _platform.Key.Returns("test");
        _platform.IsConfigured.Returns(true);
        _platform.CreateOrderAsync(Arg.Any<PlatformOrderRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var id = $"cs_{++_sessions}";
                return new PlatformOrderSession(id, new Uri($"https://pay.example/{id}"));
            });
        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
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
        _gate.GetAdmittedAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyCollection<string>>().Where(_admitted.Contains).ToHashSet(StringComparer.Ordinal));
        var services = new ServiceCollection();
        services.AddKeyedScoped(CachingTicketQueryService.InnerServiceKey, (_, _) => _holdingsInner);
        _holdings = new CachingTicketQueryService(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            _clock, NullLogger<CachingTicketQueryService>.Instance);
        _service = CreateService(_repo);
    }

    private TicketCheckoutService CreateService(ITicketCheckoutRepository repository)
    {
        var localizer = Substitute.For<IStringLocalizer<TicketsResource>>().AddFormatting();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        localizer["Tickets_Resale_Email_Invite_Body"].Returns(new LocalizedString("body", "{2}"));
        var emails = new TicketsEmails(Options.Create(new EmailSettings()), localizer, NullLogger<TicketsEmails>.Instance);

        return new TicketCheckoutService(repository, [_platform], _grants, _settings, _users, _provisioning, _userEmails,
            _preferenceReads, _preferences, _email, emails, [_delivery], _gate, _rules, localizer, _audit, _environment,
            _clock, _holdings, NullLogger<TicketCheckoutService>.Instance, _resaleProtection);
    }

    private StartCheckout Buy(
        bool forSelf = true, long? vip = null, long donation = 0, Guid? settingsId = null, string?[]? others = null) =>
        new(settingsId ?? _ticketing.Id, forSelf, vip, donation, null,
            new Uri("https://humans.example/Tickets/Buy/Order/"), "es-ES")
        { OtherEmails = others ?? [] };

    /// <summary>A person in Humans who has verified <paramref name="email"/>.</summary>
    private Guid Human(string email, string first, string last, Action<User>? shape = null)
    {
        var user = new User { Id = Guid.NewGuid(), BurnerName = first, FirstName = first, LastName = last, Email = email };
        shape?.Invoke(user);
        _users.GetUserInfoAsync(user.Id, Arg.Any<CancellationToken>()).Returns(UserInfo.Create(user, [], [], UserFixtures.Profile()));
        _userEmails.FindByAddressAsync(Arg.Is<string>(e => string.Equals(e, email, StringComparison.OrdinalIgnoreCase)), true, true,
            Arg.Any<CancellationToken>()).Returns([Row(user.Id, verified: true)]);
        return user.Id;
    }

    [HumansFact]
    public async Task Platform_check_passes_the_trimmed_event_id_and_refuses_an_unknown_key()
    {
        IReadOnlyList<PlatformFinding> ok = [new(true, "fine")];
        _platform.CheckAsync("slug", Arg.Any<CancellationToken>()).Returns(ok);

        (await _service.CheckPlatformAsync("test", " slug ", TestContext.Current.CancellationToken)).Should().BeSameAs(ok);
        (await _service.CheckPlatformAsync("nope", "slug", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [HumansFact]
    public async Task Recon_reads_the_platform_a_margin_wider_and_reports_an_unreadable_one_without_comparing()
    {
        _ticketing.PlatformEventRef = " slug ";
        var from = Instant.FromUtc(2027, 1, 1, 0, 0);
        var to = Instant.FromUtc(2027, 1, 2, 0, 0);
        _platform.ListOrdersAsync("slug", from - TicketReconBuilder.Margin, to + TicketReconBuilder.Margin, Arg.Any<CancellationToken>())
            .Returns([new PlatformOrderSummary("x", null, from, PlatformOrderState.Paid, 100, 1, 0, 0)]);

        var recon = await _service.ReconcilePlatformAsync("test", from, to, TestContext.Current.CancellationToken);

        recon!.Problems.Single().Issues.Should().Equal(TicketReconIssue.ExtraAtPlatform);
        (await _service.GetPlatformsInUseAsync(TestContext.Current.CancellationToken)).Should().Equal("test");
        (await _service.ReconcilePlatformAsync("nope", from, to, TestContext.Current.CancellationToken)).Should().BeNull();

        _platform.ListOrdersAsync(Arg.Any<string?>(), Arg.Any<Instant>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<PlatformOrderSummary>>(_ => throw new HttpRequestException("down"));
        var failed = await _service.ReconcilePlatformAsync("test", from, to, TestContext.Current.CancellationToken);
        failed!.Error.Should().Contain("down");
        failed.Rows.Should().BeEmpty();
        failed.Balanced.Should().BeFalse();
    }

    [HumansFact]
    public async Task Payment_recon_looks_past_the_period_for_a_failed_debit_coming_back()
    {
        var order = new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = _ticketing.Id,
            PlatformKey = "test",
            State = TicketCheckoutOrderState.Failed,
            TotalMinor = 30000,
            FeeMinor = 450,
            CreatedAt = Now - Duration.FromDays(20),
        };
        _repo.Orders.Add(order);
        var from = Now - Duration.FromDays(30);
        var to = Now - Duration.FromDays(10);
        _platform.GetPaymentLedgerAsync(from, to, Arg.Any<CancellationToken>()).Returns(new PaymentLedger(
            [new PaymentLedgerEntry("txn_c", Now - Duration.FromDays(20), PaymentLedgerEntryKind.Charge, 30000, 450, order.Id, null)], []));
        _platform.GetPaymentLedgerAsync(to, Now, Arg.Any<CancellationToken>()).Returns(new PaymentLedger([], []));
        var ct = TestContext.Current.CancellationToken;

        (await _service.ReconcilePaymentsAsync("test", from, to, ct))!.Problems.Should().ContainSingle()
            .Which.Issues.Should().Equal(TicketPaymentIssue.NotPaidInHumans);

        _platform.GetPaymentLedgerAsync(to, Now, Arg.Any<CancellationToken>()).Returns(new PaymentLedger(
            [new PaymentLedgerEntry("txn_back", Now - Duration.FromDays(5), PaymentLedgerEntryKind.Charge, -30000, 0, order.Id, null)], []));

        (await _service.ReconcilePaymentsAsync("test", from, to, ct))!.Balanced.Should().BeTrue();
    }

    [HumansFact]
    public async Task Platform_ledger_flags_orders_open_past_expiry_splits_failed_refunds_and_leaves_free_orders_out_of_paid()
    {
        TicketCheckoutOrder Order(TicketCheckoutOrderState state, string? platformId, Duration age, Duration expiresIn, string key = "test", long total = 1000)
        {
            var order = new TicketCheckoutOrder
            {
                Id = Guid.NewGuid(),
                TicketingSettingsId = _ticketing.Id,
                PlatformKey = key,
                State = state,
                PlatformOrderId = platformId,
                CreatedAt = Now - age,
                ExpiresAt = Now + expiresIn,
                TotalMinor = total,
            };
            _repo.Orders.Add(order);
            return order;
        }
        Order(TicketCheckoutOrderState.Created, null, Duration.FromHours(1), Duration.FromHours(1));
        var pastExpiry = Order(TicketCheckoutOrderState.Pending, "cs_1", Duration.FromHours(3), -Duration.FromHours(1));
        Order(TicketCheckoutOrderState.Pending, "cs_2", Duration.FromMinutes(5), Duration.FromMinutes(25));
        Order(TicketCheckoutOrderState.Pending, "cs_3", Duration.FromMinutes(40), -Duration.FromMinutes(10));
        var paid = Order(TicketCheckoutOrderState.Paid, "cs_4", Duration.FromDays(1), -Duration.FromDays(1));
        Order(TicketCheckoutOrderState.Paid, null, Duration.FromDays(1), -Duration.FromDays(1), total: 0);
        var refused = Order(TicketCheckoutOrderState.Paid, "cs_5", Duration.FromDays(1), -Duration.FromDays(1));
        var retried = Order(TicketCheckoutOrderState.Paid, "cs_6", Duration.FromDays(1), -Duration.FromDays(1));
        Order(TicketCheckoutOrderState.Pending, "other_1", Duration.FromDays(2), -Duration.FromDays(2), key: "other");
        _repo.Refunds.Add((Guid.NewGuid(), new TicketCheckoutRefund { Id = Guid.NewGuid(), OrderId = paid.Id, AmountMinor = 1000, State = RefundState.ManualRequired }));
        _repo.Refunds.Add((Guid.NewGuid(), new TicketCheckoutRefund { Id = Guid.NewGuid(), OrderId = paid.Id, AmountMinor = 500, State = RefundState.Succeeded }));
        _repo.Refunds.Add((Guid.NewGuid(), new TicketCheckoutRefund { Id = Guid.NewGuid(), OrderId = refused.Id, AmountMinor = 700, State = RefundState.Failed, CreatedAt = Now }));
        _repo.Refunds.Add((Guid.NewGuid(), new TicketCheckoutRefund { Id = Guid.NewGuid(), OrderId = retried.Id, AmountMinor = 300, State = RefundState.Failed, CreatedAt = Now - Duration.FromHours(2) }));
        _repo.Refunds.Add((Guid.NewGuid(), new TicketCheckoutRefund { Id = Guid.NewGuid(), OrderId = retried.Id, AmountMinor = 300, State = RefundState.Succeeded, CreatedAt = Now - Duration.FromHours(1) }));

        var ledger = await _service.GetPlatformLedgerAsync("test", TestContext.Current.CancellationToken);

        ledger!.Orders.Should().HaveCount(8).And.OnlyContain(order => order.PlatformKey == "test");
        ledger.Stuck.Should().BeEquivalentTo([pastExpiry]);
        ledger.Paid.Should().BeEquivalentTo([paid, refused, retried]);
        ledger.UnsettledRefunds.Should().ContainSingle().Which.AmountMinor.Should().Be(1000);
        ledger.FailedRefunds.Should().ContainSingle().Which.OrderId.Should().Be(refused.Id);
        (await _service.GetPlatformLedgerAsync("nope", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [HumansFact]
    public async Task Recon_keeps_reading_a_platform_and_event_its_orders_were_sold_under_after_its_ticketing_settings_moved()
    {
        var from = Instant.FromUtc(2027, 1, 1, 0, 0);
        var to = Instant.FromUtc(2027, 1, 2, 0, 0);
        _repo.Orders.Add(new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = _ticketing.Id,
            PlatformKey = "test",
            PlatformOrderId = "old/1",
            State = TicketCheckoutOrderState.Paid,
            CreatedAt = from,
        });
        _ticketing.PlatformKey = "elsewhere";
        _platform.EventRefOf("old/1").Returns("old");
        _platform.ListOrdersAsync(Arg.Any<string?>(), Arg.Any<Instant>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>()).Returns([]);

        (await _service.GetPlatformsInUseAsync(TestContext.Current.CancellationToken)).Should().Equal("test");
        var recon = await _service.ReconcilePlatformAsync("test", from, to, TestContext.Current.CancellationToken);

        recon!.Problems.Single().Issues.Should().Equal(TicketReconIssue.MissingAtPlatform);
        await _platform.Received(1).ListOrdersAsync("old", Arg.Any<Instant>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public void Price_is_the_wave_on_the_pricing_date_then_the_last_wave()
    {
        TicketPrice.For(_ticketing, new LocalDate(2027, 1, 31), []).Should().NotBeNull()
            .And.Subject.As<TicketPrice>().AmountMinor.Should().Be(22500);
        TicketPrice.For(_ticketing, new LocalDate(2027, 2, 1), [])!.AmountMinor.Should().Be(25000);
        TicketPrice.For(_ticketing, new LocalDate(2027, 5, 1), [])!.Wave.Name.Should().Be("Wave 2");
    }

    [HumansFact]
    public void Class_price_replaces_the_wave_and_discounts_stack_on_it_never_below_zero()
    {
        _ticketing.PriceRules.Add(new TicketPriceRule { GrantProgramId = DiscountProgram, Kind = TicketPriceRuleKind.ClassPrice, AmountMinor = 15000, Label = "Higher price" });
        _ticketing.PriceRules.Add(new TicketPriceRule { GrantProgramId = DiscountProgram, Kind = TicketPriceRuleKind.ClassPrice, AmountMinor = 12500, Label = "Tied price" });
        var price = TicketPrice.For(_ticketing, new LocalDate(2027, 2, 10), [LowIncomeProgram, DiscountProgram])!;
        price.ClassPrice!.Label.Should().Be("Low income");
        price.AmountMinor.Should().Be(7500);

        _ticketing.PriceRules.Add(new TicketPriceRule { GrantProgramId = DiscountProgram, Kind = TicketPriceRuleKind.Discount, AmountMinor = 20000 });
        TicketPrice.For(_ticketing, new LocalDate(2027, 2, 10), [LowIncomeProgram, DiscountProgram])!.AmountMinor.Should().Be(0);
    }

    [HumansFact]
    public async Task VIP_total_is_the_ticket_at_the_buyers_price_plus_a_donation_line_without_VAT()
    {
        var result = await _service.StartCheckoutAsync(Buy(vip: 100000), _buyerId, TestContext.Current.CancellationToken);

        result.Refusal.Should().BeNull();
        result.PaymentUrl.Should().Be(new Uri("https://pay.example/cs_1"));
        var order = _repo.Orders.Single();
        order.State.Should().Be(TicketCheckoutOrderState.Pending);
        order.PlatformOrderId.Should().Be("cs_1");
        order.TotalMinor.Should().Be(100000);
        await _platform.Received(1).CreateOrderAsync(
            Arg.Is<PlatformOrderRequest>(r =>
                r.OrderId == order.Id
                && r.BuyerName == "Ana García"
                && r.Lines.Count == 2
                && r.Lines[0].Kind == OrderLineKind.Ticket && r.Lines[0].AmountMinor == 22500 && r.Lines[0].VatRate == 0.10m
                && r.Lines[0].HolderName == "Ana García"
                && r.Lines[1].Kind == OrderLineKind.Donation && r.Lines[1].AmountMinor == 77500 && r.Lines[1].VatRate == 0m),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Others_already_in_Humans_each_get_a_ticket_in_their_legal_name_at_the_public_price()
    {
        _grants.GetActiveGrantsAsync(_buyerId, Arg.Any<CancellationToken>())
            .Returns([new ActiveGrantInfo(LowIncomeProgram, _event.Id, Now)]);
        var katie = Human("katie@example.com", "Katie", "Jones");
        var sarah = Human("sarah@example.com", "Sarah", "Lee");

        var result = await _service.StartCheckoutAsync(
            Buy(others: ["katie@example.com", " SARAH@example.com ", ""]), _buyerId, Ct);

        result.Refusal.Should().BeNull();
        var lines = _repo.Orders.Single().Lines;
        lines.Select(l => (l.HolderUserId, l.HolderName, l.AmountMinor)).Should().Equal(
            (_buyerId, "Ana García", 12500), (katie, "Katie Jones", 22500), (sarah, "Sarah Lee", 22500));
        _repo.Orders.Single().TotalMinor.Should().Be(57500);

        await PayAsync("cs_1");

        _repo.Tickets.Select(t => t.HolderUserId).Should().BeEquivalentTo(new Guid?[] { _buyerId, katie, sarah });
        await _provisioning.DidNotReceiveWithAnyArgs().FindOrCreateUserByEmailAsync(default!, default, default);
        await _provisioning.DidNotReceiveWithAnyArgs().ReplaceUnverifiedEmailAsync(default, default, default!, default, default);
    }

    [HumansFact]
    public async Task Someone_who_already_holds_a_ticket_is_refused_by_address_before_anything_is_saved()
    {
        var katie = Human("katie@example.com", "Katie", "Jones");
        await BuyAndPayAsync(Buy(), katie);
        var orders = _repo.Orders.Count;
        Human("sarah@example.com", "Sarah", "Lee");

        var result = await _service.StartCheckoutAsync(Buy(others: ["sarah@example.com", "katie@example.com"]), _buyerId, Ct);

        result.Refusal.Should().Be("Tickets_Buy_OtherHolds");
        result.RefusalArgument.Should().Be("katie@example.com");
        _repo.Orders.Should().HaveCount(orders);
    }

    [HumansFact]
    public async Task Someone_on_another_unpaid_order_counts_as_holding_a_ticket()
    {
        Human("katie@example.com", "Katie", "Jones");
        (await _service.StartCheckoutAsync(Buy(forSelf: false, others: ["katie@example.com"]), _otherBuyerId, Ct))
            .Refusal.Should().BeNull();

        (await _service.StartCheckoutAsync(Buy(others: ["katie@example.com"]), _buyerId, Ct))
            .Refusal.Should().Be("Tickets_Buy_OtherHolds");
    }

    [HumansFact]
    public async Task Only_a_single_live_verified_account_with_a_legal_name_can_be_bought_for()
    {
        Human("noname@example.com", "", "", u => { u.FirstName = ""; u.LastName = ""; });
        Human("gone@example.com", "Gone", "Person", u => u.MergedToUserId = Guid.NewGuid());
        _userEmails.FindByAddressAsync("twice@example.com", true, true, Arg.Any<CancellationToken>())
            .Returns([Row(Guid.NewGuid(), verified: true), Row(Guid.NewGuid(), verified: true)]);

        async Task<string?> RefusalAsync(string email) =>
            (await _service.StartCheckoutAsync(Buy(others: [email]), _buyerId, Ct)).Refusal;

        (await RefusalAsync("nobody@example.com")).Should().Be("Tickets_Buy_OtherNotFound");
        (await RefusalAsync("not an email")).Should().Be("Tickets_Buy_OtherNotFound");
        (await RefusalAsync("twice@example.com")).Should().Be("Tickets_Buy_OtherNotFound");
        (await RefusalAsync("gone@example.com")).Should().Be("Tickets_Buy_OtherNotFound");
        (await RefusalAsync("noname@example.com")).Should().Be("Tickets_Buy_OtherNeedsName");
        _userEmails.FindByAddressAsync("ana@example.com", true, true, Arg.Any<CancellationToken>()).Returns([Row(_buyerId, verified: true)]);
        (await RefusalAsync("ana@example.com")).Should().Be("Tickets_Buy_OtherIsYou");
        _repo.Orders.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Two_addresses_of_one_person_buy_one_ticket_and_more_than_ten_people_are_refused()
    {
        var katie = Human("katie@example.com", "Katie", "Jones");
        _userEmails.FindByAddressAsync("k@example.com", true, true, Arg.Any<CancellationToken>()).Returns([Row(katie, verified: true)]);

        (await _service.StartCheckoutAsync(Buy(others: ["katie@example.com", "k@example.com"]), _buyerId, Ct)).Refusal.Should().BeNull();
        _repo.Orders.Single().Lines.Count(l => l.HolderUserId == katie).Should().Be(1);

        var many = Enumerable.Range(0, 11).Select(i => (string?)$"p{i}@example.com").ToArray();
        (await _service.StartCheckoutAsync(Buy(forSelf: false, others: many), _otherBuyerId, Ct))
            .Refusal.Should().Be("Tickets_Buy_OthersTooMany");
    }

    [HumansFact]
    public async Task A_late_payment_for_someone_who_meanwhile_bought_their_own_is_flagged_for_the_team()
    {
        var katie = Human("katie@example.com", "Katie", "Jones");
        await _service.StartCheckoutAsync(Buy(forSelf: false, others: ["katie@example.com"]), _buyerId, Ct);
        _clock.Advance(TicketCheckoutService.CheckoutLifetime + Duration.FromMinutes(1));
        _platform.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Expired, null, null, null));
        await _service.ReconcileOpenOrdersAsync(Ct);
        _repo.Orders.Single().State.Should().Be(TicketCheckoutOrderState.Expired);
        await BuyAndPayAsync(Buy(), katie);

        await PayAsync("cs_1");

        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCheckoutNeedsAttention, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Is<string>(d => d.Contains("already held a ticket")), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    private static UserEmailRowSnapshot Row(Guid userId, bool verified) =>
        new(Guid.NewGuid(), userId, "bo@example.com", verified, null, null, IsPrimary: true, null, null, Now, Now);

    private async Task PayAsync(string session, Instant? paidAt = null)
    {
        _platform.GetStatusAsync(session, Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 100, "card", paidAt ?? Now));
        await _service.PlatformOrderChangedAsync("test", session, Ct);
    }

    [HumansFact]
    public async Task Sold_out_and_resale_only_refuse_fresh_tickets_but_not_a_donation()
    {
        _ticketing.Capacity = 0;
        (await _service.StartCheckoutAsync(Buy(), _buyerId, TestContext.Current.CancellationToken))
            .Refusal.Should().Be("Tickets_Buy_SoldOut");

        _ticketing.Capacity = 3000;
        _ticketing.Mode = TicketSalesMode.ResaleOnly;
        (await _service.StartCheckoutAsync(Buy(), _buyerId, TestContext.Current.CancellationToken))
            .Refusal.Should().Be("Tickets_Buy_ResaleOnly");
        (await _service.StartCheckoutAsync(Buy(forSelf: false, donation: 5000), _buyerId, TestContext.Current.CancellationToken))
            .Refusal.Should().BeNull();
    }

    [HumansTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StartCheckoutAsync_LocalRecordingFails_RetainsReservation(bool failSave, bool resale)
    {
        TicketCheckoutTicket? old = resale ? await OfferedTicketAsync() : null;
        if (!resale)
            _ticketing.Capacity = 1;
        var writes = _repo.OrderWrites.Count;
        if (failSave)
            _repo.FailOrderWrites = 1;
        else
            _repo.RejectEventKind = TicketCheckoutOrderEventKind.CheckoutOpened;
        var start = () => _service.StartCheckoutAsync(Buy(), resale ? _otherBuyerId : _buyerId, Ct);

        await start.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(failSave ? "Order write failed" : "Timeline write failed");

        var order = _repo.Orders.Last();
        order.State.Should().Be(TicketCheckoutOrderState.Pending);
        order.PlatformOrderId.Should().NotBeNull();
        order.PaymentUrl.Should().NotBeNull();
        _repo.OrderWrites.Should().HaveCount(writes + 1);
        await _platform.Received(resale ? 2 : 1).CreateOrderAsync(Arg.Any<PlatformOrderRequest>(), Arg.Any<CancellationToken>());
        _repo.Events.Should().NotContain(e => e.Kind == TicketCheckoutOrderEventKind.PlatformRefused);
        if (resale)
        {
            _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Reserved);
            _repo.Offers.Single().ReservedByOrderId.Should().Be(order.Id);
        }
        else
            (await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct)).Refusal.Should().Be("Tickets_Buy_SoldOut");
        _repo.RejectEventKind = null;
        _platform.GetStatusAsync(order.PlatformOrderId!, Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 0, "card", Now));

        await _service.PlatformOrderChangedAsync("test", order.PlatformOrderId!, Ct);

        order.State.Should().Be(TicketCheckoutOrderState.Paid);
        _repo.Tickets.Should().ContainSingle(t => t.OrderId == order.Id);
        if (old is not null)
            old.State.Should().Be(TicketCheckoutTicketState.Resold);
    }

    [HumansFact]
    public async Task Platform_failure_marks_the_order_failed_so_it_holds_no_capacity()
    {
        _platform.CreateOrderAsync(Arg.Any<PlatformOrderRequest>(), Arg.Any<CancellationToken>())
            .Returns<PlatformOrderSession>(_ => throw new TicketingPlatformException("nope"));

        var result = await _service.StartCheckoutAsync(Buy(), _buyerId, TestContext.Current.CancellationToken);

        result.Refusal.Should().Be("Tickets_Buy_PlatformFailed");
        _repo.Orders.Single().State.Should().Be(TicketCheckoutOrderState.Failed);
    }

    [HumansFact]
    public async Task Webhook_nudge_reads_the_platform_status_and_issues_each_ticket_once()
    {
        await _service.StartCheckoutAsync(Buy(), _buyerId, TestContext.Current.CancellationToken);
        _platform.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 455, "ideal", Now));

        await _service.PlatformOrderChangedAsync("test", "cs_1", TestContext.Current.CancellationToken);
        await _service.PlatformOrderChangedAsync("test", "cs_1", TestContext.Current.CancellationToken);

        var order = _repo.Orders.Single();
        order.State.Should().Be(TicketCheckoutOrderState.Paid);
        order.FeeMinor.Should().Be(455);
        order.PaymentMethod.Should().Be("ideal");
        _repo.Tickets.Should().ContainSingle().Which.Barcode.Should().MatchRegex("^H[0-9A-Z]{16}$");
        _repo.Tickets[0].HolderUserId.Should().Be(_buyerId);
        _repo.Tickets[0].PricePaidMinor.Should().Be(22500);
        _repo.Tickets[0].WaveName.Should().Be("Wave 1");
        await _delivery.Received(1).TicketsIssuedAsync(order.Id, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCheckoutPaid, Arg.Any<string>(), order.Id,
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task Refund_is_capped_at_the_ticket_lines_net_of_earlier_refunds_and_never_touches_donations()
    {
        await _service.StartCheckoutAsync(Buy(vip: 40000), _buyerId, TestContext.Current.CancellationToken);
        _platform.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 455, "card", Now));
        await _service.PlatformOrderChangedAsync("test", "cs_1", TestContext.Current.CancellationToken);
        var order = _repo.Orders.Single();
        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
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

        await _platform.Received(2).RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
        await _platform.Received(1).RefundAsync(
            Arg.Is<RefundRequest>(r => r.PlatformOrderId == "cs_1" && r.AmountMinor == 10000), Arg.Any<CancellationToken>());
        _repo.Refunds.Select(r => r.RefundId).Should().OnlyHaveUniqueItems();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelAsync_MissingPlatform_KeepsRemoteReservation(bool pending)
    {
        var state = pending ? TicketCheckoutOrderState.Pending : TicketCheckoutOrderState.Created;
        var old = await OfferedTicketAsync();
        var started = await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct);
        var order = _repo.Orders.Single(o => o.Id == started.OrderId);
        order.State = state;
        _platform.Key.Returns("replacement");
        _clock.Advance(Duration.FromHours(2));
        var writes = _repo.OrderWrites.Count;
        var events = _repo.Events.Count;

        var result = await _service.CancelAsync(order.Id, _otherBuyerId, Ct);

        result.Refusal.Should().Be("Tickets_Buy_CancelUnavailable");
        order.State.Should().Be(state);
        order.HoldsTickets(_clock.GetCurrentInstant()).Should().BeTrue();
        _repo.Offers.Should().ContainSingle().Which.ReservedByOrderId.Should().Be(order.Id);
        old.State.Should().Be(TicketCheckoutTicketState.Offered);
        _repo.OrderWrites.Should().HaveCount(writes);
        _repo.Events.Should().HaveCount(events);
        await _platform.DidNotReceive().CancelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CancelAsync_NoRemoteOrder_CancelsLocally()
    {
        var started = await _service.StartCheckoutAsync(Buy(), _buyerId, Ct);
        var order = _repo.Orders.Single(o => o.Id == started.OrderId);
        order.PlatformOrderId = null;
        order.State = TicketCheckoutOrderState.Created;
        _platform.Key.Returns("replacement");

        var result = await _service.CancelAsync(order.Id, _buyerId, Ct);

        result.Succeeded.Should().BeTrue();
        order.State.Should().Be(TicketCheckoutOrderState.Cancelled);
        await _platform.DidNotReceive().CancelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task A_stuck_unpaid_order_shows_on_the_buy_page_and_cancelling_it_frees_the_buyer_to_start_again()
    {
        var started = await _service.StartCheckoutAsync(Buy(), _buyerId, Ct);
        _platform.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Pending, null, null, null));

        var page = (await _service.GetBuyPagesAsync(_buyerId, Ct)).Single();
        page.GateOpeningDate.Should().Be(_event.GateOpeningDate);
        page.AlreadyHoldsTicket.Should().BeTrue();
        page.OpenOrder!.Id.Should().Be(started.OrderId!.Value);

        (await _service.CancelAsync(started.OrderId.Value, _buyerId, Ct)).Refusal.Should().BeNull();
        page = (await _service.GetBuyPagesAsync(_buyerId, Ct)).Single();
        page.OpenOrder.Should().BeNull();
        page.AlreadyHoldsTicket.Should().BeFalse();
        (await _service.StartCheckoutAsync(Buy(), _buyerId, Ct)).Refusal.Should().BeNull();
    }

    [HumansFact]
    public async Task Two_events_sell_at_once_each_with_its_own_holding_check_and_no_active_event_needed()
    {
        _settings.GetActiveEventSettings().Returns((EventSettingsInfo?)null);
        var other = new EventSettingsInfo(Guid.NewGuid(), "Elsewhere 2027", 2027, "Europe/Madrid", new LocalDate(2027, 9, 1),
            -7, 5, 8, new Dictionary<int, int>(), null, null);
        _settings.GetEventSettingsByIdAsync(other.Id, Arg.Any<CancellationToken>()).Returns(other);
        var otherTicketing = new TicketingSettings
        {
            Id = Guid.NewGuid(),
            EventSettingsId = other.Id,
            PlatformKey = "test",
            Mode = TicketSalesMode.Open,
            Capacity = 10,
            Waves = [new TicketSalesWave { Name = "Only", EndsOn = new LocalDate(2027, 8, 1), PriceMinor = 9000 }],
        };
        _repo.Settings.Add(otherTicketing);

        (await _service.StartCheckoutAsync(Buy(), _buyerId, TestContext.Current.CancellationToken)).Refusal.Should().BeNull();
        var pages = await _service.GetBuyPagesAsync(_buyerId, TestContext.Current.CancellationToken);

        pages.Select(p => p.EventName).Should().Equal("Nowhere 2027", "Elsewhere 2027");
        pages[0].AlreadyHoldsTicket.Should().BeTrue();
        pages[1].AlreadyHoldsTicket.Should().BeFalse();
        (await _service.StartCheckoutAsync(Buy(settingsId: otherTicketing.Id), _buyerId, TestContext.Current.CancellationToken))
            .Refusal.Should().BeNull();
        _repo.Orders.Last().TotalMinor.Should().Be(9000);
        await _platform.Received(1).CreateOrderAsync(
            Arg.Is<PlatformOrderRequest>(r => r.EventId == other.Id), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Timeline_records_each_step_with_who_did_it()
    {
        _platform.CreateOrderAsync(Arg.Any<PlatformOrderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderSession("cs_1", new Uri("https://pay.example/cs_1"), new Uri("https://admin.example/cs_1")));
        await _service.StartCheckoutAsync(Buy(), _buyerId, TestContext.Current.CancellationToken);
        _platform.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 455, "card", Now));
        await _service.PlatformOrderChangedAsync("test", "cs_1", TestContext.Current.CancellationToken);
        var order = _repo.Orders.Single();
        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Succeeded, "re_1", null));
        _platform.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 455, "card", Now, RefundedMinor: 5000));
        var admin = Guid.NewGuid();
        await _service.RefundAsync(order.Id, 5000, "can't come", admin, TestContext.Current.CancellationToken);

        var detail = (await _service.GetOrderDetailAsync(order.Id, TestContext.Current.CancellationToken))!;
        order.AdminUrl.Should().Be("https://admin.example/cs_1");
        detail.Timeline.Select(e => e.Kind).Should().Equal(
            TicketCheckoutOrderEventKind.Created,
            TicketCheckoutOrderEventKind.CheckoutOpened,
            TicketCheckoutOrderEventKind.StateChanged,
            TicketCheckoutOrderEventKind.TicketsIssued,
            TicketCheckoutOrderEventKind.RefundRequested,
            TicketCheckoutOrderEventKind.RefundSettled,
            TicketCheckoutOrderEventKind.RefundedTotalChanged);
        detail.Timeline[0].ActorUserId.Should().Be(_buyerId);
        detail.Timeline[2].ActorUserId.Should().BeNull();
        detail.Timeline[4].ActorUserId.Should().Be(admin);
        detail.Timeline.Should().NotContain(e => e.Detail.Contains("García", StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task Refund_of_an_unpaid_order_is_refused_without_calling_the_platform()
    {
        await _service.StartCheckoutAsync(Buy(), _buyerId, TestContext.Current.CancellationToken);

        (await _service.RefundAsync(_repo.Orders.Single().Id, 100, "nope", Guid.NewGuid(), TestContext.Current.CancellationToken))
            .Succeeded.Should().BeFalse();
        await _platform.DidNotReceive().RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [HumansTheory]
    [InlineData("Valid", 1)]
    [InlineData("Offered", 1)]
    [InlineData("Resold", 0)]
    [InlineData("Void", 0)]
    public async Task GetHeldTicketCountAsync_TicketState_MatchesBuyPage(string state, int count)
    {
        var ticket = await PaidAdultAsync();
        ticket.State = Enum.Parse<TicketCheckoutTicketState>(state);

        (await _service.GetHeldTicketCountAsync(_buyerId, Ct)).Should().Be(count);
        (await _service.GetBuyPagesAsync(_buyerId, Ct)).Single().AlreadyHoldsTicket.Should().Be(count > 0);
    }

    [HumansTheory]
    [InlineData(true, 4)]
    [InlineData(false, 6)]
    public async Task GetHeldTicketCountAsync_EventScope_CountsRelevantEvents(bool active, int count)
    {
        _ticketing.Mode = TicketSalesMode.Closed;
        if (!active)
            _settings.GetActiveEventSettings().Returns((EventSettingsInfo?)null);
        var previous = new TicketingSettings { Id = Guid.NewGuid(), EventSettingsId = Guid.NewGuid(), Mode = TicketSalesMode.Closed };
        var upcoming = new TicketingSettings { Id = Guid.NewGuid(), EventSettingsId = Guid.NewGuid(), Mode = TicketSalesMode.Open };
        _repo.Settings.AddRange([previous, upcoming]);
        foreach (var (sale, tickets) in new[] { (_ticketing, 1), (previous, 2), (upcoming, 3) })
            for (var i = 0; i < tickets; i++)
                _repo.Tickets.Add(new TicketCheckoutTicket
                {
                    Id = Guid.NewGuid(),
                    TicketingSettingsId = sale.Id,
                    HolderUserId = _buyerId,
                    State = TicketCheckoutTicketState.Valid,
                });
        _repo.Tickets.Add(new TicketCheckoutTicket
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = upcoming.Id,
            HolderUserId = _otherBuyerId,
            State = TicketCheckoutTicketState.Valid,
        });

        (await _service.GetHeldTicketCountAsync(_buyerId, Ct)).Should().Be(count);
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetBuyOrderAsync_Viewer_ReturnsPermittedData(bool buyer)
    {
        var ownLine = new TicketCheckoutOrderLine { Kind = OrderLineKind.Ticket, HolderUserId = _otherBuyerId, AmountMinor = 22500 };
        var otherLine = new TicketCheckoutOrderLine { Kind = OrderLineKind.Ticket, HolderUserId = _buyerId, AmountMinor = 22500 };
        var donation = new TicketCheckoutOrderLine { Kind = OrderLineKind.Donation, AmountMinor = 1000 };
        var order = new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            BuyerUserId = _buyerId,
            BuyerEmail = "buyer@example.org",
            TicketingSettingsId = _ticketing.Id,
            PlatformKey = "test",
            PlatformOrderId = "cs_1",
            State = TicketCheckoutOrderState.Paid,
            TotalMinor = 46000,
            PaymentUrl = "https://pay.example/cs_1",
            Lines = [ownLine, otherLine, donation],
        };
        _repo.Orders.Add(order);
        var ownTicket = new TicketCheckoutTicket { Id = Guid.NewGuid(), OrderId = order.Id, HolderUserId = _otherBuyerId };
        var otherTicket = new TicketCheckoutTicket { Id = Guid.NewGuid(), OrderId = order.Id, HolderUserId = _buyerId };
        _repo.Tickets.AddRange([ownTicket, otherTicket]);
        _platform.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, null, null, Now, RefundedMinor: 1000));

        var result = (await _service.GetBuyOrderAsync(order.Id, buyer ? _buyerId : _otherBuyerId, Ct))!;

        result.IsBuyer.Should().Be(buyer);
        result.Lines.Should().Equal(buyer ? [ownLine, otherLine, donation] : [ownLine]);
        result.Tickets.Should().Equal(buyer ? [ownTicket, otherTicket] : [ownTicket]);
        result.TotalMinor.Should().Be(buyer ? 46000 : 0);
        result.TicketTotalMinor.Should().Be(buyer ? 45000 : 0);
        result.RefundedMinor.Should().Be(buyer ? 1000 : 0);
        result.PaymentUrl.Should().Be(buyer ? order.PaymentUrl : null);
        order.Lines.Should().Equal(ownLine, otherLine, donation);
        _repo.Tickets.Should().Equal(ownTicket, otherTicket);
        await _platform.Received(1).GetStatusAsync("cs_1", Ct);
    }

    [HumansFact]
    public async Task GetBuyOrderAsync_UnrelatedViewer_DoesNotRefresh()
    {
        await _service.StartCheckoutAsync(Buy(), _buyerId, Ct);

        (await _service.GetBuyOrderAsync(_repo.Orders.Single().Id, _otherBuyerId, Ct)).Should().BeNull();

        await _platform.DidNotReceiveWithAnyArgs().GetStatusAsync(default!);
    }

    [HumansFact]
    public async Task GetBuyOrderAsync_MissingOrder_ReturnsNull()
    {
        (await _service.GetBuyOrderAsync(Guid.NewGuid(), _buyerId, Ct)).Should().BeNull();

        await _platform.DidNotReceiveWithAnyArgs().GetStatusAsync(default!);
    }

    [HumansFact]
    public async Task GetBuyOrderAsync_NewPayment_ReturnsIssuedTickets()
    {
        await _service.StartCheckoutAsync(Buy(), _buyerId, Ct);
        var order = _repo.Orders.Single();
        _platform.GetStatusAsync(order.PlatformOrderId!, Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 0, "card", Now));

        var result = (await _service.GetBuyOrderAsync(order.Id, _buyerId, Ct))!;

        result.State.Should().Be(TicketCheckoutOrderState.Paid);
        result.Tickets.Should().ContainSingle().Which.HolderUserId.Should().Be(_buyerId);
    }

    private async Task<TicketCheckoutOrder> BuyAndPayAsync(StartCheckout command, Guid userId)
    {
        var result = await _service.StartCheckoutAsync(command, userId, Ct);
        result.Refusal.Should().BeNull();
        var order = _repo.Orders.Single(o => o.Id == result.OrderId);
        _platform.GetStatusAsync(order.PlatformOrderId!, Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 0, "card", _clock.GetCurrentInstant()));
        await _service.PlatformOrderChangedAsync("test", order.PlatformOrderId!, Ct);
        return order;
    }

    private async Task<TicketCheckoutTicket> OfferedTicketAsync(StartCheckout? command = null)
    {
        var order = await BuyAndPayAsync(command ?? Buy(), _buyerId);
        var adult = _repo.Tickets.Single(t => t.OrderId == order.Id && t.Kind == TicketHolderKind.Adult);
        (await _service.OfferForResaleAsync(adult.Id, _buyerId, Ct)).Refusal.Should().BeNull();
        _ticketing.Mode = TicketSalesMode.ResaleOnly;
        return adult;
    }

    /// <summary>The evening batch, run once the new buyer's order has held long enough.</summary>
    private async Task<ResaleRefundBatch> RefundBatchAsync()
    {
        _clock.Advance(Duration.FromHours(12));
        return await _service.RunResaleRefundBatchAsync(Ct);
    }

    [HumansFact]
    public async Task A_link_sent_to_several_people_sells_to_the_first_and_offers_the_next_resale_offer_to_the_rest()
    {
        var dan = Human("dan@example.com", "Dan", "Ruiz");
        var cy = Human("cy@example.com", "Cy", "Doe");
        var dansOrder = await BuyAndPayAsync(Buy(), dan);
        var mine = await OfferedTicketAsync();
        var dans = _repo.Tickets.Single(t => t.OrderId == dansOrder.Id);
        (await _service.OfferForResaleAsync(dans.Id, dan, Ct)).Refusal.Should().BeNull();
        var link = new Uri("https://h.example/r/");
        var sent = new List<EmailMessage>();
        await _email.SendAsync(Arg.Do<EmailMessage>(sent.Add), Arg.Any<CancellationToken>());
        (await _service.InviteBuyersAsync(mine.Id, _buyerId, ["bo@example.com", "cy@example.com"], link, "en", Ct)).Refusal.Should().BeNull();
        string TokenFor(string address) => sent.Single(m => string.Equals(m.RecipientEmail, address, StringComparison.Ordinal)).HtmlBody[link.ToString().Length..];
        var boToken = TokenFor("bo@example.com");
        var cyToken = TokenFor("cy@example.com");

        (await _service.StartCheckoutAsync(Buy() with { DirectedToken = boToken }, cy, Ct))
            .Refusal.Should().Be("Tickets_Resale_InviteOtherEmail", "a link works only for the address it was sent to");
        var cysOrder = await BuyAndPayAsync(Buy() with { DirectedToken = cyToken }, cy);

        cysOrder.Lines.Single().ResaleOfferId.Should().Be(_repo.Offers.Single(o => o.TicketId == mine.Id).Id);
        mine.State.Should().Be(TicketCheckoutTicketState.Resold);
        var page = await _service.GetDirectedBuyPageAsync(boToken, _otherBuyerId, Ct);
        page!.InviteTaken.Should().BeTrue();
        page.Unavailable.Should().BeNull();
        var bosOrder = await BuyAndPayAsync(Buy() with { DirectedToken = boToken }, _otherBuyerId);
        bosOrder.Lines.Single().ResaleOfferId.Should().Be(_repo.Offers.Single(o => o.TicketId == dans.Id).Id);
        dans.State.Should().Be(TicketCheckoutTicketState.Resold);
    }

    [HumansFact]
    public async Task A_taken_link_still_sells_the_next_resale_offer_to_a_class_priced_buyer_while_fresh_stock_is_open()
    {
        var dan = Human("dan@example.com", "Dan", "Ruiz");
        var cy = Human("cy@example.com", "Cy", "Doe");
        var dansOrder = await BuyAndPayAsync(Buy(), dan);
        var mine = await OfferedTicketAsync();
        var dans = _repo.Tickets.Single(t => t.OrderId == dansOrder.Id);
        (await _service.OfferForResaleAsync(dans.Id, dan, Ct)).Refusal.Should().BeNull();
        var link = new Uri("https://h.example/r/");
        var sent = new List<EmailMessage>();
        await _email.SendAsync(Arg.Do<EmailMessage>(sent.Add), Arg.Any<CancellationToken>());
        (await _service.InviteBuyersAsync(mine.Id, _buyerId, ["bo@example.com", "cy@example.com"], link, "en", Ct)).Refusal.Should().BeNull();
        string TokenFor(string address) => sent.Single(m => string.Equals(m.RecipientEmail, address, StringComparison.Ordinal)).HtmlBody[link.ToString().Length..];
        var boToken = TokenFor("bo@example.com");
        await BuyAndPayAsync(Buy() with { DirectedToken = TokenFor("cy@example.com") }, cy);
        _ticketing.Mode = TicketSalesMode.Open;
        _grants.GetActiveGrantsAsync(_otherBuyerId, Arg.Any<CancellationToken>())
            .Returns([new ActiveGrantInfo(LowIncomeProgram, _event.Id, Now)]);

        var page = await _service.GetDirectedBuyPageAsync(boToken, _otherBuyerId, Ct);
        var bosOrder = await BuyAndPayAsync(Buy() with { DirectedToken = boToken }, _otherBuyerId);

        page!.InviteTaken.Should().BeTrue();
        var line = bosOrder.Lines.Single();
        line.ResaleOfferId.Should().Be(_repo.Offers.Single(o => o.TicketId == dans.Id).Id, "a link buys from the queue, like the invitation it was");
        line.AmountMinor.Should().Be(page.OwnPrice!.AmountMinor).And.NotBe(12500);
    }

    [HumansFact]
    public async Task The_evening_batch_waits_for_the_hold_refunds_once_and_audits_its_totals()
    {
        var old = await OfferedTicketAsync();
        await BuyAndPayAsync(Buy(), _otherBuyerId);

        (await _service.RunResaleRefundBatchAsync(Ct)).Should().Be(new ResaleRefundBatch(0, 0, 0, 1));
        await _platform.DidNotReceive().RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());

        (await RefundBatchAsync()).Should().Be(new ResaleRefundBatch(1, 22500, 0, 0));
        (await _service.RunResaleRefundBatchAsync(Ct)).Should().Be(new ResaleRefundBatch(0, 0, 0, 0));

        await _platform.Received(1).RefundAsync(
            Arg.Is<RefundRequest>(r => r.PlatformOrderId == "cs_1" && r.AmountMinor == 22500), Arg.Any<CancellationToken>());
        _repo.Offers.Single().RefundId.Should().NotBeNull();
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketResaleRefundBatch, Arg.Any<string>(), _ticketing.Id,
            Arg.Is<string>(d => d.Contains("1 refund", StringComparison.Ordinal)), Arg.Any<string>());
        old.State.Should().Be(TicketCheckoutTicketState.Resold);
    }

    [HumansFact]
    public async Task The_evening_batch_holds_back_a_refund_when_the_buyers_order_was_refunded()
    {
        var old = await OfferedTicketAsync();
        var buyer = await BuyAndPayAsync(Buy(), _otherBuyerId);
        (await _service.RefundAsync(buyer.Id, 22500, "changed their mind", _adminId, Ct)).Succeeded.Should().BeTrue();
        _platform.ClearReceivedCalls();

        (await RefundBatchAsync()).HeldBack.Should().Be(1);
        (await _service.RunResaleRefundBatchAsync(Ct)).HeldBack.Should().Be(0);

        await _platform.DidNotReceive().RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
        var offer = _repo.Offers.Single();
        offer.RefundId.Should().Be(offer.Id, "held back for good: a later batch never pays it on its own");
        _repo.Refunds.Select(r => r.Refund).Should().ContainSingle(r => r.Id == offer.Id && r.OrderId == old.OrderId && r.State == RefundState.Failed
            && r.FailureReason!.StartsWith("Resale refund held back", StringComparison.Ordinal));
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCheckoutNeedsAttention, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Is<string>(d => d.StartsWith("Resale refund held back", StringComparison.Ordinal)), Arg.Any<string>());
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketResaleRefundBatch, Arg.Any<string>(), _ticketing.Id,
            Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task The_evening_batch_reads_the_buyers_order_from_the_platform_so_a_missed_chargeback_holds_the_refund_back()
    {
        await OfferedTicketAsync();
        var buyer = await BuyAndPayAsync(Buy(), _otherBuyerId);
        _platform.GetStatusAsync(buyer.PlatformOrderId!, Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 0, "card", Now, ChargedBackMinor: 22500));

        (await RefundBatchAsync()).HeldBack.Should().Be(1);

        await _platform.DidNotReceive().RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
        _repo.Orders.Single(o => o.Id == buyer.Id).ChargedBackMinor.Should().Be(22500);
    }

    [HumansFact]
    public async Task The_evening_batch_pays_nothing_while_the_platform_cannot_be_read_and_pays_once_it_can()
    {
        await OfferedTicketAsync();
        var buyer = await BuyAndPayAsync(Buy(), _otherBuyerId);
        _platform.GetStatusAsync(buyer.PlatformOrderId!, Arg.Any<CancellationToken>())
            .Returns<PlatformOrderStatus>(_ => throw new HttpRequestException("down"));

        (await RefundBatchAsync()).Should().Be(new ResaleRefundBatch(0, 0, 0, 1));
        await _platform.DidNotReceive().RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());

        _platform.GetStatusAsync(buyer.PlatformOrderId!, Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 0, "card", Now));
        (await _service.RunResaleRefundBatchAsync(Ct)).Should().Be(new ResaleRefundBatch(1, 22500, 0, 0));
    }

    [HumansFact]
    public async Task ReconcileOpenOrdersAsync_SoldNoticeHandoffFails_Retries()
    {
        var old = await OfferedTicketAsync();
        _delivery.ResaleSoldAsync(old.Id, Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("queue down"), _ => Task.CompletedTask);
        await BuyAndPayAsync(Buy(), _otherBuyerId);

        await _service.ReconcileOpenOrdersAsync(Ct);

        await _delivery.Received(2).ResaleSoldAsync(old.Id, 22500, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOpenOrdersAsync_UnsoldJobQueued_RetainsPendingNotice()
    {
        var old = await OfferedTicketAsync();
        _ticketing.Mode = TicketSalesMode.Closed;

        await _service.ReconcileOpenOrdersAsync(Ct);
        _repo.Offers.Single().UnsoldNotifiedAt.Should().BeNull();
        await _service.ReconcileOpenOrdersAsync(Ct);

        await _delivery.Received(2).ResaleUnsoldAsync(old.Id, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOpenOrdersAsync_CancelledEvent_SuppressesSoldNoticeRetries()
    {
        var old = await OfferedTicketAsync();
        await BuyAndPayAsync(Buy(), _otherBuyerId);
        await _service.CancelEventAsync(_ticketing.Id, "Storm", _adminId, Ct);
        _delivery.ClearReceivedCalls();

        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);

        await _delivery.DidNotReceive().ResaleSoldAsync(old.Id, Arg.Any<long>(), Arg.Any<CancellationToken>());
        _repo.Offers.Single().SoldNotificationPending.Should().BeTrue("suppression is not an accepted delivery");
    }

    [HumansFact]
    public async Task RecordDeliveryAsync_SoldOutboxAccepted_CompletesNotice()
    {
        var old = await OfferedTicketAsync();
        await BuyAndPayAsync(Buy(), _otherBuyerId);
        var offer = _repo.Offers.Single();
        offer.SoldNotificationPending.Should().BeTrue();
        _delivery.ClearReceivedCalls();
        await _service.ReconcileOpenOrdersAsync(Ct);
        await _delivery.Received(1).ResaleSoldAsync(old.Id, 22500, Arg.Any<CancellationToken>());

        await _service.RecordDeliveryAsync(old.OrderId, TicketCheckoutOrderEventKind.ResaleSold, "Notice queued", null, Ct, offer.Id);
        await _service.RecordDeliveryAsync(old.OrderId, TicketCheckoutOrderEventKind.ResaleSold, "Notice queued", null, Ct, offer.Id);
        offer.SoldNotificationPending.Should().BeFalse();
        _delivery.ClearReceivedCalls();
        await _service.ReconcileOpenOrdersAsync(Ct);

        await _delivery.DidNotReceive().ResaleSoldAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        _repo.Events.Count(e => e.Kind == TicketCheckoutOrderEventKind.ResaleSold && string.Equals(e.Detail, "Notice queued", StringComparison.Ordinal)).Should().Be(1);
    }

    [HumansFact]
    public async Task ReconcileOpenOrdersAsync_SaleInterrupted_RecoversNotice()
    {
        var old = await OfferedTicketAsync();
        _repo.RejectEventKind = TicketCheckoutOrderEventKind.ResaleSold;
        var pay = () => BuyAndPayAsync(Buy(), _otherBuyerId);
        await pay.Should().ThrowAsync<InvalidOperationException>().WithMessage("Timeline write failed");
        _repo.Offers.Single().SoldNotificationPending.Should().BeTrue();
        _repo.RejectEventKind = null;

        await _service.ReconcileOpenOrdersAsync(Ct);

        await _delivery.Received(1).ResaleSoldAsync(old.Id, 22500, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileOpenOrdersAsync_LegacySoldNotice_DoesNotBackfill()
    {
        await OfferedTicketAsync();
        await BuyAndPayAsync(Buy(), _otherBuyerId);
        _repo.Offers.Single().SoldNotificationPending = null;
        _delivery.ClearReceivedCalls();

        await _service.ReconcileOpenOrdersAsync(Ct);

        await _delivery.DidNotReceive().ResaleSoldAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task At_sales_close_the_holder_of_an_unsold_offer_is_told_once()
    {
        var old = await OfferedTicketAsync();
        await _service.ReconcileOpenOrdersAsync(Ct);
        await _delivery.DidNotReceiveWithAnyArgs().ResaleUnsoldAsync(default);

        _ticketing.Mode = TicketSalesMode.Closed;
        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.RecordDeliveryAsync(old.OrderId, TicketCheckoutOrderEventKind.ResaleUnsold, "Notice queued", null, Ct,
            _repo.Offers.Single().Id);
        await _service.ReconcileOpenOrdersAsync(Ct);

        await _delivery.Received(1).ResaleUnsoldAsync(old.Id, Arg.Any<CancellationToken>());
        _repo.Offers.Single().UnsoldNotifiedAt.Should().Be(Now);
        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Open);
        _repo.Events.Should().Contain(e => e.OrderId == old.OrderId && e.Kind == TicketCheckoutOrderEventKind.ResaleUnsold);
    }

    [HumansFact]
    public async Task An_unsold_notice_that_could_not_be_queued_is_tried_again_by_the_next_sweep()
    {
        var old = await OfferedTicketAsync();
        _ticketing.Mode = TicketSalesMode.Closed;
        _delivery.ResaleUnsoldAsync(old.Id, Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("queue down"), _ => Task.CompletedTask);

        await _service.ReconcileOpenOrdersAsync(Ct);
        _repo.Offers.Single().UnsoldNotifiedAt.Should().BeNull();
        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.RecordDeliveryAsync(old.OrderId, TicketCheckoutOrderEventKind.ResaleUnsold, "Notice queued", null, Ct,
            _repo.Offers.Single().Id);
        await _service.ReconcileOpenOrdersAsync(Ct);

        await _delivery.Received(2).ResaleUnsoldAsync(old.Id, Arg.Any<CancellationToken>());
        _repo.Offers.Single().UnsoldNotifiedAt.Should().Be(Now);
        _repo.Events.Count(e => e.Kind == TicketCheckoutOrderEventKind.ResaleUnsold).Should().Be(1);
    }

    [HumansFact]
    public async Task An_order_that_fails_or_lapses_tells_the_buyer_once()
    {
        await _service.StartCheckoutAsync(Buy(), _buyerId, Ct);
        _platform.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Failed, null, null, null));

        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);

        await _delivery.Received(1).PaymentEndedAsync(_repo.Orders.Single().Id, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Blank_child_name_boxes_are_ignored()
    {
        _ticketing.ChildTypes = [new TicketChildType { Name = "Child", UnderAge = 13, PriceMinor = 5000 }];
        var order = await BuyAndPayAsync(Buy() with { Children = [new(null, null), new("  ", null), new("Kid García", null)] }, _buyerId);

        order.TotalMinor.Should().Be(27500);
        _repo.Tickets.Should().ContainSingle(t => t.Kind == TicketHolderKind.Child);
    }

    [HumansFact]
    public async Task Each_child_is_priced_by_the_child_type_the_buyer_picks()
    {
        _ticketing.ChildTypes =
        [
            new TicketChildType { Name = "Under 6", UnderAge = 6, PriceMinor = 0 },
            new TicketChildType { Name = "Under 13", UnderAge = 13, PriceMinor = 5000 },
        ];
        (await _service.StartCheckoutAsync(Buy() with { Children = [new("Kid García", null)] }, _buyerId, Ct))
            .Refusal.Should().Be("Tickets_Buy_Invalid", "with two types the buyer must pick one");
        (await _service.StartCheckoutAsync(Buy() with { Children = [new("Kid García", "Teen")] }, _buyerId, Ct))
            .Refusal.Should().Be("Tickets_Buy_Invalid");

        var order = await BuyAndPayAsync(Buy() with { Children = [new("Baby García", "Under 6"), new("Kid García", "Under 13")] }, _buyerId);

        order.TotalMinor.Should().Be(27500);
        _repo.Tickets.Where(t => t.Kind == TicketHolderKind.Child).Select(t => (t.HolderName, t.WaveName, t.PricePaidMinor))
            .Should().BeEquivalentTo([("Baby García", "Under 6", 0L), ("Kid García", "Under 13", 5000L)]);
    }

    [HumansFact]
    public async Task A_donation_goes_to_one_of_the_events_purposes_or_is_general()
    {
        (await _service.StartCheckoutAsync(Buy(donation: 1000) with { DonationPurpose = "Art" }, _buyerId, Ct))
            .Refusal.Should().Be("Tickets_Buy_Invalid", "the event names no purposes");

        _ticketing.DonationPurposes = ["Art", "Low income pool"];
        var order = await BuyAndPayAsync(Buy(donation: 1000) with { DonationPurpose = "Low income pool" }, _buyerId);

        order.Lines.Single(l => l.Kind == OrderLineKind.Donation).DonationPurpose.Should().Be("Low income pool");
    }

    [HumansFact]
    public async Task Child_types_and_donation_purposes_need_distinct_names()
    {
        TicketingSettingsSave Save() => new(_ticketing.Id, _event.Id, "test", null, TicketSalesMode.Open, null, 3000, 0.10m,
            _ticketing.Waves, [], []);

        (await _service.SaveTicketingSettingsAsync(Save() with
        {
            ChildTypes = [new() { Name = "Child", UnderAge = 13, PriceMinor = 0 }, new() { Name = "child", UnderAge = 6, PriceMinor = 0 }],
        }, _adminId, Ct)).Refusal.Should().Contain("Child tickets need distinct names");
        (await _service.SaveTicketingSettingsAsync(Save() with { DonationPurposes = ["Art", " Art "] }, _adminId, Ct))
            .Refusal.Should().Contain("Donation purposes need distinct names");

        (await _service.SaveTicketingSettingsAsync(Save() with
        {
            ChildTypes = [new() { Name = "Under 13 ", UnderAge = 13, PriceMinor = 5000 }, new() { Name = "Under 6", UnderAge = 6, PriceMinor = 0 }],
            DonationPurposes = [" Art "],
        }, _adminId, Ct)).Refusal.Should().BeNull();
        var saved = await _repo.GetTicketingSettingsAsync(_ticketing.Id, Ct);
        saved!.ChildTypes.Select(c => c.Name).Should().Equal("Under 6", "Under 13");
        saved.DonationPurposes.Should().Equal("Art");
    }

    [HumansFact]
    public async Task Children_are_issued_with_the_adult_and_need_a_child_price()
    {
        (await _service.StartCheckoutAsync(Buy() with { Children = [new("Kid García", null)] }, _buyerId, Ct))
            .Refusal.Should().Be("Tickets_Buy_Invalid");

        _ticketing.ChildTypes = [new TicketChildType { Name = "Child", UnderAge = 13, PriceMinor = 5000 }];
        var order = await BuyAndPayAsync(Buy() with { Children = [new("Kid García", null)] }, _buyerId);

        order.TotalMinor.Should().Be(27500);
        var adult = _repo.Tickets.Single(t => t.Kind == TicketHolderKind.Adult);
        var child = _repo.Tickets.Single(t => t.Kind == TicketHolderKind.Child);
        child.ParentTicketId.Should().Be(adult.Id);
        child.HolderName.Should().Be("Kid García");
        child.PricePaidMinor.Should().Be(5000);
    }

    private static TicketRulesRead RulesAt(string version) =>
        new(new TicketRules("TicketRules/2027", version, new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "# Normas", ["en"] = "# Rules" }), null);

    [HumansFact]
    public async Task Ticket_rules_from_the_legal_repo_are_shown_accepted_on_every_checkout_and_their_version_kept_on_the_order()
    {
        _ticketing.TermsFolder = "TicketRules/2027";
        _rules.GetAsync("TicketRules/2027", Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(RulesAt("abc123"));
        _rules.Permalink("TicketRules/2027", "abc123").Returns(new Uri("https://github.com/o/legal/tree/abc123/TicketRules/2027"));

        var page = (await _service.GetBuyPagesAsync(_buyerId, Ct)).Single();
        page.Unavailable.Should().BeNull();
        page.Terms!.Texts.Keys.Should().BeEquivalentTo("es", "en");
        (await _service.StartCheckoutAsync(Buy() with { TermsFolder = "TicketRules/2027", TermsVersion = "abc123" }, _buyerId, Ct)).Refusal.Should().Be("Tickets_Buy_TermsRequired");
        _repo.Orders.Should().BeEmpty();

        (await _service.StartCheckoutAsync(Buy() with { AcceptTerms = true, TermsFolder = "TicketRules/2027", TermsVersion = "abc123" }, _buyerId, Ct)).Refusal.Should().BeNull();
        var order = _repo.Orders.Single();
        order.TermsFolder.Should().Be("TicketRules/2027");
        order.TermsVersion.Should().Be("abc123");
        _repo.Events.Should().Contain(e => e.Kind == TicketCheckoutOrderEventKind.TermsAccepted
            && e.Detail.Contains("https://github.com/o/legal/tree/abc123/TicketRules/2027"));
    }

    [HumansFact]
    public async Task Ticket_rules_changed_since_the_page_was_shown_or_unreadable_stop_the_checkout()
    {
        _ticketing.TermsFolder = "TicketRules/2027";
        _rules.GetAsync("TicketRules/2027", Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(RulesAt("def456"));

        (await _service.StartCheckoutAsync(Buy() with { AcceptTerms = true, TermsFolder = "TicketRules/2027", TermsVersion = "abc123" }, _buyerId, Ct))
            .Refusal.Should().Be("Tickets_Buy_TermsChanged");

        _rules.GetAsync("TicketRules/2027", Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(RulesAt("abc123"));
        (await _service.StartCheckoutAsync(Buy() with { AcceptTerms = true, TermsFolder = "TicketRules/2026", TermsVersion = "abc123" }, _buyerId, Ct))
            .Refusal.Should().Be("Tickets_Buy_TermsChanged");

        _rules.GetAsync("TicketRules/2027", Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(TicketRulesRead.Failed("GitHub could not be reached."));
        (await _service.GetBuyPagesAsync(_buyerId, Ct)).Single().Unavailable.Should().Be("Tickets_Buy_TermsUnavailable");
        (await _service.StartCheckoutAsync(Buy() with { AcceptTerms = true, TermsFolder = "TicketRules/2027", TermsVersion = "abc123" }, _buyerId, Ct))
            .Refusal.Should().Be("Tickets_Buy_TermsUnavailable");
        _repo.Orders.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Without_a_rules_folder_nothing_is_accepted_or_recorded()
    {
        (await _service.StartCheckoutAsync(Buy(), _buyerId, Ct)).Refusal.Should().BeNull();

        _repo.Orders.Single().TermsVersion.Should().BeNull();
        _repo.Events.Should().NotContain(e => e.Kind == TicketCheckoutOrderEventKind.TermsAccepted);
        await _rules.DidNotReceiveWithAnyArgs().GetAsync(default!);
    }

    [HumansFact]
    public async Task Switched_off_tickets_for_others_donations_and_mailing_list_are_refused_and_not_offered()
    {
        _ticketing.Switches = new TicketingSwitches { PartnerTickets = false, Donations = false, MailingListOptIn = false };

        var page = (await _service.GetBuyPagesAsync(_buyerId, Ct)).Single();
        page.OffersOthers.Should().BeFalse();
        page.OffersDonations.Should().BeFalse();
        page.AskMarketing.Should().BeFalse();
        Human("katie@example.com", "Katie", "Jones");
        (await _service.StartCheckoutAsync(Buy(others: ["katie@example.com"]), _buyerId, Ct))
            .Refusal.Should().Be("Tickets_Buy_Invalid");
        (await _service.StartCheckoutAsync(Buy(vip: 40000), _buyerId, Ct)).Refusal.Should().Be("Tickets_Buy_Invalid");
        (await _service.StartCheckoutAsync(Buy(donation: 1000), _buyerId, Ct)).Refusal.Should().Be("Tickets_Buy_Invalid");
        _repo.Orders.Should().BeEmpty();

        await _service.StartCheckoutAsync(Buy() with { MarketingOptIn = true }, _buyerId, Ct);
        await _preferences.DidNotReceiveWithAnyArgs().UpdatePreferenceAsync(default, default, default, default!);
    }

    [HumansFact]
    public async Task Ticketing_settings_without_switches_sell_the_default_scheme()
    {
        _ticketing.Switches = null;

        var page = (await _service.GetBuyPagesAsync(_buyerId, Ct)).Single();
        page.OffersOthers.Should().BeTrue();
        page.OffersDonations.Should().BeTrue();
    }

    [HumansFact]
    public async Task Resale_switched_off_refuses_resale_offers_and_resale_only_mode()
    {
        _ticketing.Switches = new TicketingSwitches { Resale = ResaleScheme.Off };
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        var ticket = _repo.Tickets.Single(t => t.OrderId == order.Id);

        (await _service.GetHeldTicketsAsync(_buyerId, Ct)).Single().CanOffer.Should().BeFalse();
        (await _service.OfferForResaleAsync(ticket.Id, _buyerId, Ct)).Refusal.Should().Be("Tickets_Resale_Closed");
        ticket.State.Should().Be(TicketCheckoutTicketState.Valid);

        var save = new TicketingSettingsSave(_ticketing.Id, _event.Id, "test", null, TicketSalesMode.ResaleOnly, null, 100, 0.1m,
            [], [], [])
        {
            Switches = new TicketingSwitches { Resale = ResaleScheme.Off },
        };
        (await _service.SaveTicketingSettingsAsync(save, _buyerId, Ct)).Refusal.Should().Be("Resale only needs resale switched on.");
    }

    [HumansFact]
    public async Task A_new_ticket_rules_folder_must_be_a_plain_path_holding_spanish_and_english_rules()
    {
        TicketingSettingsSave Save(string folder) => new(_ticketing.Id, _event.Id, "test", null, TicketSalesMode.Open, null, 3000, 0.10m,
            _ticketing.Waves, [], [])
        { TermsFolder = folder };

        (await _service.SaveTicketingSettingsAsync(Save("../secrets"), _buyerId, Ct)).Refusal.Should().Contain("path in the legal repo");
        _rules.GetAsync("TicketRules/2027", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(TicketRulesRead.Failed("It has no English file (NORMAS-en.md)."));
        (await _service.SaveTicketingSettingsAsync(Save("TicketRules/2027"), _buyerId, Ct)).Refusal.Should().Contain("It has no English file");

        _rules.GetAsync("TicketRules/2027", Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(RulesAt("abc123"));
        (await _service.SaveTicketingSettingsAsync(Save("TicketRules/2027"), _buyerId, Ct)).Refusal.Should().BeNull();
        _repo.Settings.Single().TermsFolder.Should().Be("TicketRules/2027");
        await _rules.Received().GetAsync("TicketRules/2027", true, Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task OfferForResaleAsync_ClosingBoundary_MatchesHolderAction(int seconds, bool available)
    {
        var ticket = await PaidAdultAsync();
        _ticketing.ResaleOpenedAt = null;
        _ticketing.SalesCloseAt = Now + Duration.FromSeconds(seconds);

        var canOffer = (await _service.GetHeldTicketsAsync(_buyerId, Ct)).Single().CanOffer;
        (await _service.OfferForResaleAsync(ticket.Id, _buyerId, Ct)).Succeeded.Should().Be(available);
        canOffer.Should().Be(available);
    }

    [HumansTheory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task InviteBuyersAsync_ClosingBoundary_MatchesHolderAction(int seconds, bool available)
    {
        var ticket = await OfferedTicketAsync();
        _ticketing.SalesCloseAt = Now + Duration.FromSeconds(seconds);

        var canInvite = (await _service.GetHeldTicketsAsync(_buyerId, Ct)).Single().CanInvite;
        var result = await _service.InviteBuyersAsync(ticket.Id, _buyerId, ["bo@example.com"], new Uri("https://h.example/r/"), "en", Ct);
        result.Succeeded.Should().Be(available);
        canInvite.Should().Be(available);
        if (!available)
            result.Refusal.Should().Be("Tickets_Resale_Closed");
    }

    [HumansFact]
    public async Task GetHeldTicketsAsync_ResalePaused_HidesInvitationAction()
    {
        var ticket = await OfferedTicketAsync();
        _ticketing.Mode = TicketSalesMode.Open;
        _ticketing.ResaleOpenedAt = null;

        (await _service.GetHeldTicketsAsync(_buyerId, Ct)).Single().CanInvite.Should().BeFalse();
        (await _service.InviteBuyersAsync(ticket.Id, _buyerId, ["bo@example.com"], new Uri("https://h.example/r/"), "en", Ct))
            .Refusal.Should().Be("Tickets_Resale_NotOpenYet");
    }

    [HumansTheory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void ResaleSelling_ClosingBoundary_UsesSnapshotTime(int seconds, bool selling)
    {
        _ticketing.SalesCloseAt = Now + Duration.FromSeconds(seconds);
        var model = new Humans.Tickets.Controllers.ResaleViewModel(_ticketing.Id, "Event", _ticketing, [],
            new Dictionary<Guid, TicketCheckoutOrder>(), Now);

        model.ResaleSelling.Should().Be(selling);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResaleAction_EligibilityCrossesDeadline_RefusesBeforeWrite(bool invite)
    {
        var ticket = invite ? await OfferedTicketAsync() : await PaidAdultAsync();
        _ticketing.SalesCloseAt = Now + Duration.FromSeconds(1);
        var reads = 0;
        _gate.GetAdmittedAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (++reads == (invite ? 2 : 1))
                _clock.Advance(Duration.FromSeconds(2));
            return new HashSet<string>(StringComparer.Ordinal);
        });

        var result = invite
            ? await _service.InviteBuyersAsync(ticket.Id, _buyerId, ["bo@example.com"], new Uri("https://h.example/r/"), "en", Ct)
            : await _service.OfferForResaleAsync(ticket.Id, _buyerId, Ct);

        result.Refusal.Should().Be("Tickets_Resale_Closed");
        if (invite)
            _repo.Offers.Single().Invites.Should().BeEmpty();
        else
            _repo.Offers.Should().BeEmpty();
    }

    [HumansFact]
    public async Task OfferForResaleAsync_TicketWriteFails_WritesNoOffer()
    {
        var ticket = await PaidAdultAsync();
        _repo.RejectTicketWrites.Add(ticket.Id);
        var offer = () => _service.OfferForResaleAsync(ticket.Id, _buyerId, Ct);

        await offer.Should().ThrowAsync<InvalidOperationException>().WithMessage("Ticket write failed");

        _repo.Offers.Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData((int)TicketResaleOfferState.Open)]
    [InlineData((int)TicketResaleOfferState.Reserved)]
    public async Task OfferForResaleAsync_ActiveOffer_RefusesDuplicate(int state)
    {
        var ticket = await PaidAdultAsync();
        _repo.Offers.Add(new TicketResaleOffer
        {
            Id = Guid.NewGuid(),
            TicketId = ticket.Id,
            TicketingSettingsId = _ticketing.Id,
            HolderUserId = _buyerId,
            State = (TicketResaleOfferState)state,
        });

        (await _service.OfferForResaleAsync(ticket.Id, _buyerId, Ct)).Refusal.Should().Be("Tickets_Resale_NotOfferable");
        _repo.Offers.Should().ContainSingle();
    }

    [HumansTheory]
    [InlineData((int)TicketCheckoutTicketState.Valid)]
    [InlineData((int)TicketCheckoutTicketState.Void)]
    [InlineData((int)TicketCheckoutTicketState.Resold)]
    [InlineData(-1)]
    public async Task StartCheckoutAsync_InvalidOfferedTicket_RefusesResale(int state)
    {
        var ticket = await OfferedTicketAsync();
        if (state < 0)
            _repo.Tickets.Remove(ticket);
        else
            ticket.State = (TicketCheckoutTicketState)state;

        (await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct)).Refusal.Should().Be("Tickets_Buy_ResaleOnly");
        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Open);
    }

    [HumansFact]
    public async Task StartCheckoutAsync_DuplicateActiveOffers_RefusesResale()
    {
        var ticket = await OfferedTicketAsync();
        _repo.Offers.Add(new TicketResaleOffer
        {
            Id = Guid.NewGuid(),
            TicketId = ticket.Id,
            TicketingSettingsId = _ticketing.Id,
            HolderUserId = _buyerId,
            State = TicketResaleOfferState.Open,
        });

        (await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct)).Refusal.Should().Be("Tickets_Buy_ResaleOnly");
        _repo.Offers.Should().OnlyContain(o => o.State == TicketResaleOfferState.Open);
    }

    [HumansFact]
    public async Task GetDirectedBuyPageAsync_InvalidOfferedTicket_ShowsTakenLink()
    {
        var ticket = await OfferedTicketAsync();
        _repo.Offers.Single().Invites = [new TicketResaleInvite
        {
            Email = "bo@example.com", TokenHash = TicketCheckoutService.HashToken("legacy-token"), SentAt = Now,
        }];
        ticket.State = TicketCheckoutTicketState.Valid;

        (await _service.GetDirectedBuyPageAsync("legacy-token", _otherBuyerId, Ct))!.InviteTaken.Should().BeTrue();
        (await _service.StartCheckoutAsync(Buy() with { DirectedToken = "legacy-token" }, _otherBuyerId, Ct))
            .Refusal.Should().Be("Tickets_Buy_ResaleOnly");
    }

    [HumansTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WithdrawOfferAsync_TicketWriteFails_WritesNoOffer(int action)
    {
        var ticket = await OfferedTicketAsync();
        var offer = _repo.Offers.Single();
        _repo.RejectTicketWrites.Add(ticket.Id);
        _repo.OfferWrites.Clear();
        Func<Task> withdraw = action switch
        {
            0 => () => _service.WithdrawOfferAsync(ticket.Id, _buyerId, Ct),
            1 => () => _service.WithdrawOfferForHolderAsync(offer.Id, "Holder asked", _adminId, Ct),
            _ => SaveResaleOffAsync,
        };

        await withdraw.Should().ThrowAsync<InvalidOperationException>().WithMessage("Ticket write failed");

        _repo.OfferWrites.Should().BeEmpty();
    }

    [HumansFact]
    public async Task ReconcileOpenOrdersAsync_ResaleOff_WithdrawsOpenOffer()
    {
        var ticket = await OfferedTicketAsync();
        _ticketing.Mode = TicketSalesMode.Open;
        _ticketing.Switches = new TicketingSwitches { Resale = ResaleScheme.Off };

        await _service.ReconcileOpenOrdersAsync(Ct);

        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Withdrawn);
        ticket.State.Should().Be(TicketCheckoutTicketState.Valid);
    }

    [HumansTheory]
    [InlineData((int)TicketCheckoutTicketState.Void)]
    [InlineData((int)TicketCheckoutTicketState.Resold)]
    public async Task ReconcileOpenOrdersAsync_RetiredTicket_KeepsRetiredState(int state)
    {
        var ticket = await OfferedTicketAsync();
        ticket.State = (TicketCheckoutTicketState)state;
        _ticketing.Mode = TicketSalesMode.Open;
        _ticketing.Switches = new TicketingSwitches { Resale = ResaleScheme.Off };

        await _service.ReconcileOpenOrdersAsync(Ct);

        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Withdrawn);
        ticket.State.Should().Be((TicketCheckoutTicketState)state);
    }

    [HumansFact]
    public async Task Switching_resale_off_withdraws_open_offers_and_hands_the_tickets_back()
    {
        var ticket = await OfferedTicketAsync();

        await SaveResaleOffAsync();

        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Withdrawn);
        ticket.State.Should().Be(TicketCheckoutTicketState.Valid);
        _repo.Events.Should().Contain(e => e.Kind == TicketCheckoutOrderEventKind.ResaleWithdrawn && e.Detail.Contains("resale switched off"));
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketResaleWithdrawn, Arg.Any<string>(), ticket.OrderId,
            Arg.Is<string>(d => d.Contains("resale switched off")), _buyerId, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task An_offer_in_an_unpaid_order_when_resale_is_switched_off_is_withdrawn_once_that_order_ends()
    {
        var ticket = await OfferedTicketAsync();
        var pending = await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct);

        await SaveResaleOffAsync();
        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Reserved);

        _platform.GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Pending, null, null, null));
        (await _service.CancelAsync(pending.OrderId!.Value, _otherBuyerId, Ct)).Refusal.Should().BeNull();
        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Withdrawn);
        ticket.State.Should().Be(TicketCheckoutTicketState.Valid);
    }

    private async Task SaveResaleOffAsync()
    {
        var save = new TicketingSettingsSave(_ticketing.Id, _event.Id, "test", null, TicketSalesMode.Open, null, 3000, 0.10m,
            _ticketing.Waves, [], [])
        {
            Switches = new TicketingSwitches { Resale = ResaleScheme.Off },
        };
        (await _service.SaveTicketingSettingsAsync(save, _buyerId, Ct)).Refusal.Should().BeNull();
    }

    [HumansFact]
    public async Task Switching_to_queue_only_ends_live_invitations_and_the_offer_sells_from_the_queue()
    {
        var old = await OfferedTicketAsync();
        EmailMessage? sent = null;
        await _email.SendAsync(Arg.Do<EmailMessage>(m => sent = m), Arg.Any<CancellationToken>());
        (await _service.InviteBuyersAsync(old.Id, _buyerId, ["bo@example.com"], new Uri("https://h.example/r/"), "en", Ct))
            .Refusal.Should().BeNull();
        var token = sent!.HtmlBody["https://h.example/r/".Length..];
        (await _service.GetDirectedBuyPageAsync(token, _otherBuyerId, Ct)).Should().NotBeNull();

        var save = new TicketingSettingsSave(_ticketing.Id, _event.Id, "test", null, TicketSalesMode.ResaleOnly, null, 3000, 0.10m,
            _ticketing.Waves, [], [])
        {
            Switches = new TicketingSwitches { Resale = ResaleScheme.Queue },
        };
        (await _service.SaveTicketingSettingsAsync(save, _buyerId, Ct)).Refusal.Should().BeNull();

        _repo.Offers.Single().Invites.Should().BeEmpty();
        _repo.Events.Should().Contain(e => e.Kind == TicketCheckoutOrderEventKind.ResaleInviteCancelled);
        var held = (await _service.GetHeldTicketsAsync(_buyerId, Ct)).Single();
        held.DirectedActive.Should().BeFalse();
        held.CanInvite.Should().BeFalse();
        (await _service.InviteBuyersAsync(old.Id, _buyerId, ["bo@example.com"], new Uri("https://h.example/r/"), "en", Ct))
            .Refusal.Should().Be("Tickets_Resale_NotFound");
        (await _service.GetDirectedBuyPageAsync(token, _otherBuyerId, Ct)).Should().BeNull();
        (await _service.StartCheckoutAsync(Buy() with { DirectedToken = token }, _otherBuyerId, Ct))
            .Refusal.Should().Be("Tickets_Resale_InviteNotFound");

        var queued = await BuyAndPayAsync(Buy(), _otherBuyerId);
        queued.Lines.Single().ResaleOfferId.Should().Be(_repo.Offers.Single().Id);
    }

    [HumansFact]
    public async Task Mailing_list_opt_in_is_saved_only_when_the_buyer_has_no_choice_on_record()
    {
        await _service.StartCheckoutAsync(Buy() with { MarketingOptIn = true }, _buyerId, Ct);

        await _preferences.Received(1).UpdatePreferenceAsync(_buyerId, MessageCategory.Marketing, false, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Resale_sells_the_oldest_offer_first_voids_children_and_refunds_the_holder_what_they_paid_in_the_evening_batch()
    {
        _ticketing.ChildTypes = [new TicketChildType { Name = "Child", UnderAge = 13, PriceMinor = 5000 }];
        var old = await OfferedTicketAsync(Buy() with { Children = [new("Kid García", null)] });
        var offer = _repo.Offers.Single();
        offer.RefundOwedMinor.Should().Be(27500);
        old.State.Should().Be(TicketCheckoutTicketState.Offered);

        var order = await BuyAndPayAsync(Buy(), _otherBuyerId);

        order.Lines.Single().ResaleOfferId.Should().Be(offer.Id);
        offer.State.Should().Be(TicketResaleOfferState.Sold);
        offer.SoldToOrderId.Should().Be(order.Id);
        old.State.Should().Be(TicketCheckoutTicketState.Resold);
        _repo.Tickets.Single(t => t.Kind == TicketHolderKind.Child).State.Should().Be(TicketCheckoutTicketState.Void);
        _repo.Tickets.Single(t => t.OrderId == order.Id).HolderUserId.Should().Be(_otherBuyerId);
        await _delivery.Received(1).ResaleSoldAsync(old.Id, 27500, Arg.Any<CancellationToken>());
        await _platform.DidNotReceive().RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
        offer.RefundId.Should().BeNull();

        await RefundBatchAsync();

        await _platform.Received(1).RefundAsync(
            Arg.Is<RefundRequest>(r => r.PlatformOrderId == "cs_1" && r.AmountMinor == 27500), Arg.Any<CancellationToken>());
        offer.RefundId.Should().NotBeNull();
        _repo.Events.Should().Contain(e => e.OrderId == old.OrderId && e.Kind == TicketCheckoutOrderEventKind.ResaleSold);
    }

    [HumansFact]
    public async Task An_offer_held_by_an_unpaid_order_cannot_be_withdrawn_and_goes_back_to_the_queue_when_it_is_cancelled()
    {
        var old = await OfferedTicketAsync();
        var pending = await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct);
        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Reserved);

        (await _service.WithdrawOfferAsync(old.Id, _buyerId, Ct)).Refusal.Should().Be("Tickets_Resale_InCheckout");
        _platform.GetStatusAsync("cs_2", Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Pending, null, null, null));
        (await _service.CancelAsync(pending.OrderId!.Value, _otherBuyerId, Ct)).Refusal.Should().BeNull();
        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Open);

        (await _service.WithdrawOfferAsync(old.Id, _buyerId, Ct)).Refusal.Should().BeNull();
        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Withdrawn);
        old.State.Should().Be(TicketCheckoutTicketState.Valid);
    }

    [HumansFact]
    public async Task A_ticket_used_at_the_gate_cannot_be_offered_for_resale()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        var ticket = _repo.Tickets.Single(t => t.OrderId == order.Id);
        _admitted.Add(ticket.Barcode);

        (await _service.OfferForResaleAsync(ticket.Id, _buyerId, Ct)).Refusal.Should().Be("Tickets_Resale_NotOfferable");
        ticket.State.Should().Be(TicketCheckoutTicketState.Valid);
        _repo.Offers.Should().BeEmpty();
    }

    [HumansFact]
    public async Task OfferForResaleAsync_AdmittedCompanion_Refuses()
    {
        var adult = await PaidAdultAsync();
        (await _service.IssueCompanionAsync(adult.Id, "Helper Ruiz", null, "Support", false, _adminId, Ct)).Succeeded.Should().BeTrue();
        var companion = _repo.Tickets.Single(t => t.Kind == TicketHolderKind.Companion);
        companion.OrderId.Should().NotBe(adult.OrderId);
        _admitted.Add(companion.Barcode);

        var result = await _service.OfferForResaleAsync(adult.Id, _buyerId, Ct);

        result.Refusal.Should().Be("Tickets_Resale_NotOfferable");
        adult.State.Should().Be(TicketCheckoutTicketState.Valid);
        _repo.Offers.Should().BeEmpty();
    }

    [HumansFact]
    public async Task InviteBuyersAsync_AdmittedCompanion_Refuses()
    {
        var adult = await PaidAdultAsync();
        (await _service.IssueCompanionAsync(adult.Id, "Helper Ruiz", null, "Support", false, _adminId, Ct)).Succeeded.Should().BeTrue();
        (await _service.OfferForResaleAsync(adult.Id, _buyerId, Ct)).Succeeded.Should().BeTrue();
        _admitted.Add(_repo.Tickets.Single(t => t.Kind == TicketHolderKind.Companion).Barcode);

        var result = await _service.InviteBuyersAsync(adult.Id, _buyerId, ["bo@example.com"],
            new Uri("https://h.example/r/"), "en", Ct);

        result.Refusal.Should().Be("Tickets_Resale_NotOfferable");
        _repo.Offers.Single().Invites.Should().BeEmpty();
        _repo.Offers.Single().DirectedInviteCount.Should().Be(0);
    }

    [HumansFact]
    public async Task StartCheckoutAsync_DirectedAdmittedCompanion_Refuses()
    {
        var adult = await PaidAdultAsync();
        (await _service.IssueCompanionAsync(adult.Id, "Helper Ruiz", null, "Support", false, _adminId, Ct)).Succeeded.Should().BeTrue();
        (await _service.OfferForResaleAsync(adult.Id, _buyerId, Ct)).Succeeded.Should().BeTrue();
        EmailMessage? sent = null;
        _email.SendAsync(Arg.Do<EmailMessage>(m => sent = m), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var link = new Uri("https://h.example/Tickets/Buy/Resale/");
        (await _service.InviteBuyersAsync(adult.Id, _buyerId, ["bo@example.com"], link, "en", Ct)).Succeeded.Should().BeTrue();
        var token = sent!.HtmlBody[link.AbsoluteUri.Length..];
        _ticketing.Mode = TicketSalesMode.ResaleOnly;
        _admitted.Add(_repo.Tickets.Single(t => t.Kind == TicketHolderKind.Companion).Barcode);
        var orders = _repo.Orders.Count;

        var result = await _service.StartCheckoutAsync(Buy() with { DirectedToken = token }, _otherBuyerId, Ct);

        result.Refusal.Should().Be("Tickets_Buy_ResaleOnly");
        _repo.Orders.Should().HaveCount(orders);
        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Open);
    }

    [HumansFact]
    public async Task A_ticket_whose_child_was_used_at_the_gate_cannot_be_offered()
    {
        _ticketing.ChildTypes = [new TicketChildType { Name = "Child", UnderAge = 13, PriceMinor = 5000 }];
        var order = await BuyAndPayAsync(Buy() with { Children = [new("Kid García", null)] }, _buyerId);
        var adult = _repo.Tickets.Single(t => t.OrderId == order.Id && t.Kind == TicketHolderKind.Adult);
        _admitted.Add(_repo.Tickets.Single(t => t.OrderId == order.Id && t.Kind == TicketHolderKind.Child).Barcode);

        (await _service.OfferForResaleAsync(adult.Id, _buyerId, Ct)).Refusal.Should().Be("Tickets_Resale_NotOfferable");
    }

    [HumansFact]
    public async Task An_offered_ticket_used_at_the_gate_never_sells_or_takes_an_invitation()
    {
        var old = await OfferedTicketAsync();
        _admitted.Add(old.Barcode);

        (await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct)).Refusal.Should().Be("Tickets_Buy_ResaleOnly");
        (await _service.InviteBuyersAsync(old.Id, _buyerId, ["bo@example.com"], new Uri("https://h.example/r/"), "en", Ct))
            .Refusal.Should().Be("Tickets_Resale_NotOfferable");
        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Open);
    }

    [HumansTheory]
    [InlineData((int)TicketCheckoutOrderState.Created, true, true)]
    [InlineData((int)TicketCheckoutOrderState.Pending, true, true)]
    [InlineData((int)TicketCheckoutOrderState.Processing, false, true)]
    [InlineData((int)TicketCheckoutOrderState.Paid, false, true)]
    [InlineData((int)TicketCheckoutOrderState.Created, false, false)]
    [InlineData((int)TicketCheckoutOrderState.Pending, false, false)]
    [InlineData((int)TicketCheckoutOrderState.Failed, true, false)]
    [InlineData((int)TicketCheckoutOrderState.Expired, true, false)]
    [InlineData((int)TicketCheckoutOrderState.Cancelled, true, false)]
    [InlineData(-1, true, true)]
    [InlineData(-2, true, true)]
    public async Task FindIssuedTicketAsync_ClosedSales_RespectsReservedOrder(int state, bool remote, bool blocked)
    {
        var adult = AddTicket(holder: _buyerId, state: TicketCheckoutTicketState.Offered);
        var child = AddTicket(TicketHolderKind.Child, parent: adult.Id);
        var companion = AddTicket(TicketHolderKind.Companion, parent: adult.Id);
        var orderId = Guid.NewGuid();
        if (state >= 0)
            _repo.Orders.Add(new TicketCheckoutOrder
            {
                Id = orderId,
                TicketingSettingsId = _ticketing.Id,
                State = (TicketCheckoutOrderState)state,
                PlatformOrderId = remote ? "remote" : null,
                ExpiresAt = Now - Duration.FromHours(1),
            });
        _repo.Offers.Add(new TicketResaleOffer
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = _ticketing.Id,
            TicketId = adult.Id,
            State = TicketResaleOfferState.Reserved,
            ReservedByOrderId = state == -2 ? null : orderId,
        });
        _ticketing.SalesCloseAt = Now;

        var listed = await _service.GetIssuedTicketsForEventAsync(_event.Id, Ct);
        foreach (var ticket in new[] { adult, child, companion })
        {
            var found = await _service.FindIssuedTicketAsync(ticket.Barcode, Ct);
            found!.IsValid.Should().Be(!blocked);
            found.InResaleQueue.Should().Be(blocked);
            listed.Single(t => t.Id == ticket.Id).Should().Be(found);
        }
    }

    [HumansTheory]
    [InlineData((int)TicketCheckoutTicketState.Valid)]
    [InlineData((int)TicketCheckoutTicketState.Offered)]
    public async Task FindIssuedTicketAsync_SoldBeforeRetirement_RefusesOldTickets(int state)
    {
        var adult = AddTicket(holder: _buyerId, state: (TicketCheckoutTicketState)state);
        var child = AddTicket(TicketHolderKind.Child, parent: adult.Id);
        var companion = AddTicket(TicketHolderKind.Companion, parent: adult.Id);
        _repo.Offers.Add(new TicketResaleOffer
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = _ticketing.Id,
            TicketId = adult.Id,
            State = TicketResaleOfferState.Sold,
        });
        _ticketing.Mode = TicketSalesMode.Closed;

        var listed = await _service.GetIssuedTicketsForEventAsync(_event.Id, Ct);
        foreach (var ticket in new[] { adult, child, companion })
        {
            var found = await _service.FindIssuedTicketAsync(ticket.Barcode, Ct);
            found!.IsValid.Should().BeFalse();
            found.InResaleQueue.Should().BeFalse();
            listed.Single(t => t.Id == ticket.Id).Should().Be(found);
        }
    }

    [HumansFact]
    public async Task An_offer_whose_child_was_used_at_the_gate_takes_no_invitation()
    {
        _ticketing.ChildTypes = [new TicketChildType { Name = "Child", UnderAge = 13, PriceMinor = 5000 }];
        var order = await BuyAndPayAsync(Buy() with { Children = [new("Kid García", null)] }, _buyerId);
        var adult = _repo.Tickets.Single(t => t.OrderId == order.Id && t.Kind == TicketHolderKind.Adult);
        (await _service.OfferForResaleAsync(adult.Id, _buyerId, Ct)).Refusal.Should().BeNull();
        _admitted.Add(_repo.Tickets.Single(t => t.OrderId == order.Id && t.Kind == TicketHolderKind.Child).Barcode);

        (await _service.InviteBuyersAsync(adult.Id, _buyerId, ["bo@example.com"], new Uri("https://h.example/r/"), "en", Ct))
            .Refusal.Should().Be("Tickets_Resale_NotOfferable");
    }

    [HumansFact]
    public async Task Only_the_holder_can_offer_withdraw_or_invite()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        var ticket = _repo.Tickets.Single(t => t.OrderId == order.Id);

        (await _service.OfferForResaleAsync(ticket.Id, _otherBuyerId, Ct)).Refusal.Should().Be("Tickets_Resale_NotFound");
        (await _service.OfferForResaleAsync(ticket.Id, _buyerId, Ct)).Refusal.Should().BeNull();
        (await _service.WithdrawOfferAsync(ticket.Id, _otherBuyerId, Ct)).Refusal.Should().Be("Tickets_Resale_NotFound");
        (await _service.InviteBuyersAsync(ticket.Id, _otherBuyerId, ["x@example.com"], new Uri("https://h.example/r/"), "en", Ct))
            .Refusal.Should().Be("Tickets_Resale_NotFound");
        (await _service.CancelInviteAsync(ticket.Id, _otherBuyerId, Ct)).Refusal.Should().Be("Tickets_Resale_NotFound");
        ticket.State.Should().Be(TicketCheckoutTicketState.Offered);
    }

    [HumansFact]
    public async Task InviteBuyersAsync_QueueFailure_RetriesWithoutInvalidating()
    {
        var adult = await OfferedTicketAsync();
        var sent = new List<EmailMessage>();
        var failing = true;
        _email.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            sent.Add(call.Arg<EmailMessage>());
            return failing ? throw new InvalidOperationException("Queue unavailable") : Task.CompletedTask;
        });
        var link = new Uri("https://h.example/Tickets/Buy/Resale/");
        var invite = () => _service.InviteBuyersAsync(adult.Id, _buyerId, ["bo@example.com"], link, "en", Ct);
        await invite.Should().ThrowAsync<InvalidOperationException>();
        var originalToken = sent.Single().HtmlBody[link.AbsoluteUri.Length..];
        failing = false;

        (await invite()).Succeeded.Should().BeTrue();

        sent.Should().HaveCount(2);
        sent[0].DeliveryId.Should().NotBeNull().And.Be(sent[1].DeliveryId);
        _repo.Offers.Single().DirectedInviteCount.Should().Be(1);
        _repo.Offers.Single().Invites.Should().ContainSingle();
        var retriedToken = sent[1].HtmlBody[link.AbsoluteUri.Length..];
        (await _service.GetDirectedBuyPageAsync(originalToken, _otherBuyerId, Ct))!.Unavailable.Should().BeNull();
        (await _service.GetDirectedBuyPageAsync(retriedToken, _otherBuyerId, Ct))!.Unavailable.Should().BeNull();
        (await _service.GetDirectedBuyPageAsync(retriedToken, _buyerId, Ct))!.Unavailable.Should().Be("Tickets_Resale_InviteOtherEmail");
        var stored = System.Text.Json.JsonSerializer.Serialize(_repo.Offers.Single().Invites);
        stored.Should().NotContain(originalToken).And.NotContain(retriedToken);
        (await _service.CancelInviteAsync(adult.Id, _buyerId, Ct)).Succeeded.Should().BeTrue();
        (await _service.GetDirectedBuyPageAsync(originalToken, _otherBuyerId, Ct)).Should().BeNull();
        (await _service.GetDirectedBuyPageAsync(retriedToken, _otherBuyerId, Ct)).Should().BeNull();
    }

    [HumansFact]
    public async Task InviteBuyersAsync_PartialQueueFailure_RetriesOnlyPending()
    {
        var adult = await OfferedTicketAsync();
        var sent = new List<EmailMessage>();
        var failing = true;
        _email.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            sent.Add(call.Arg<EmailMessage>());
            return failing && sent.Count == 2 ? throw new InvalidOperationException("Queue unavailable") : Task.CompletedTask;
        });
        var link = new Uri("https://h.example/Tickets/Buy/Resale/");
        var invite = () => _service.InviteBuyersAsync(adult.Id, _buyerId, ["bo@example.com", "cy@example.com"], link, "en", Ct);
        await invite.Should().ThrowAsync<InvalidOperationException>();
        failing = false;

        (await invite()).Succeeded.Should().BeTrue();

        sent.Select(m => m.RecipientEmail).Should().Equal("bo@example.com", "cy@example.com", "cy@example.com");
        sent[1].DeliveryId.Should().NotBeNull().And.Be(sent[2].DeliveryId);
        _repo.Offers.Single().DirectedInviteCount.Should().Be(2);
        (await invite()).Refusal.Should().Be("Tickets_Resale_InviteAlready");
        sent.Should().HaveCount(3);
    }

    [HumansFact]
    public async Task GetDirectedBuyPageAsync_ProtectedInvitation_RejectsForgery()
    {
        var adult = await OfferedTicketAsync();
        EmailMessage? sent = null;
        _email.SendAsync(Arg.Do<EmailMessage>(m => sent = m), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var link = new Uri("https://h.example/Tickets/Buy/Resale/");
        (await _service.InviteBuyersAsync(adult.Id, _buyerId, ["bo@example.com"], link, "en", Ct)).Succeeded.Should().BeTrue();
        var invite = _repo.Offers.Single().Invites.Single();
        var token = sent!.HtmlBody[link.AbsoluteUri.Length..];
        var tampered = token.ToCharArray();
        tampered[token.Length / 2] = tampered[token.Length / 2] == 'A' ? 'B' : 'A';
        var wrongPurpose = _resaleProtection.CreateProtector("AnotherPurpose")
            .Protect($"{_repo.Offers.Single().Id:D}/{invite.DeliveryId:D}");
        var absentInvite = _resaleProtection.CreateProtector("TicketResaleInvitation")
            .Protect($"{_repo.Offers.Single().Id:D}/{Guid.NewGuid():D}");
        foreach (var invalid in new[] { new string(tampered), wrongPurpose, absentInvite })
        {
            (await _service.GetDirectedBuyPageAsync(invalid, _otherBuyerId, Ct)).Should().BeNull();
            (await _service.StartCheckoutAsync(Buy() with { DirectedToken = invalid }, _otherBuyerId, Ct))
                .Refusal.Should().Be("Tickets_Resale_InviteNotFound");
        }
    }

    [HumansFact]
    public async Task InviteBuyersAsync_PendingAtLimit_RetriesWithoutNewAllowance()
    {
        var adult = await OfferedTicketAsync();
        var addresses = Enumerable.Range(0, 10).Select(i => $"invite{i}@example.com").ToArray();
        var failing = true;
        _email.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()).Returns(_ =>
            failing ? throw new InvalidOperationException("Queue unavailable") : Task.CompletedTask);
        var link = new Uri("https://h.example/r/");
        var invite = () => _service.InviteBuyersAsync(adult.Id, _buyerId, addresses, link, "en", Ct);
        await invite.Should().ThrowAsync<InvalidOperationException>();
        failing = false;

        (await invite()).Succeeded.Should().BeTrue();

        _repo.Offers.Single().DirectedInviteCount.Should().Be(10);
        _repo.Offers.Single().Invites.Should().OnlyContain(i => i.DeliveryPending == false);
        (await _service.InviteBuyersAsync(adult.Id, _buyerId, ["extra@example.com"], link, "en", Ct))
            .Refusal.Should().Be("Tickets_Resale_InviteLimit");
    }

    [HumansFact]
    public async Task GetDirectedBuyPageAsync_LegacyToken_StillWorks()
    {
        var adult = await OfferedTicketAsync();
        var offer = _repo.Offers.Single();
        const string token = "legacy-opaque-token";
        offer.Invites = [new TicketResaleInvite { Email = "bo@example.com", TokenHash = TicketCheckoutService.HashToken(token), SentAt = Now }];
        offer.DirectedInviteCount = 1;

        (await _service.GetDirectedBuyPageAsync(token, _otherBuyerId, Ct))!.Unavailable.Should().BeNull();
        (await _service.InviteBuyersAsync(adult.Id, _buyerId, ["bo@example.com"], new Uri("https://h.example/r/"), "en", Ct))
            .Refusal.Should().Be("Tickets_Resale_InviteAlready");
    }

    [HumansFact]
    public async Task A_directed_offer_sells_only_to_the_invited_email_through_the_link()
    {
        var old = await OfferedTicketAsync();
        EmailMessage? sent = null;
        await _email.SendAsync(Arg.Do<EmailMessage>(m => sent = m), Arg.Any<CancellationToken>());

        (await _service.InviteBuyersAsync(old.Id, _buyerId, ["bo@example.com"], new Uri("https://h.example/Tickets/Buy/Resale/"), "en", Ct))
            .Refusal.Should().BeNull();

        sent!.RecipientEmail.Should().Be("bo@example.com");
        var token = sent.HtmlBody["https://h.example/Tickets/Buy/Resale/".Length..];
        _repo.Offers.Single().Invites.Single().TokenHash.Should().Be(TicketCheckoutService.HashToken(token)).And.NotBe(token);
        (await _service.StartCheckoutAsync(Buy() with { DirectedToken = "wrong" }, _otherBuyerId, Ct))
            .Refusal.Should().Be("Tickets_Resale_InviteNotFound");

        var order = await BuyAndPayAsync(Buy() with { DirectedToken = token }, _otherBuyerId);

        _repo.Offers.Single().DirectedUsedAt.Should().Be(Now);
        old.State.Should().Be(TicketCheckoutTicketState.Resold);
        _repo.Events.Should().Contain(e => e.OrderId == order.Id && e.Kind == TicketCheckoutOrderEventKind.ResaleInviteUsed);
        _repo.Events.Should().Contain(e => e.OrderId == old.OrderId && e.Kind == TicketCheckoutOrderEventKind.ResaleInvited);
    }

    [HumansFact]
    public async Task An_invited_ticket_stays_in_the_queue_and_the_first_buyer_gets_it()
    {
        var old = await OfferedTicketAsync();
        EmailMessage? sent = null;
        await _email.SendAsync(Arg.Do<EmailMessage>(m => sent = m), Arg.Any<CancellationToken>());
        await _service.InviteBuyersAsync(old.Id, _buyerId, ["someone@example.com"], new Uri("https://h.example/r/"), "en", Ct);
        var token = sent!.HtmlBody["https://h.example/r/".Length..];

        _clock.Advance(Duration.FromDays(60));
        await _service.ReconcileOpenOrdersAsync(Ct);
        _repo.Offers.Single().Invites.Should().ContainSingle();

        var queued = await BuyAndPayAsync(Buy(), _otherBuyerId);

        queued.Lines.Single().ResaleOfferId.Should().Be(_repo.Offers.Single().Id);
        _repo.Offers.Single().DirectedUsedAt.Should().BeNull();
        _repo.Events.Should().NotContain(e => e.Kind == TicketCheckoutOrderEventKind.ResaleInviteUsed);
        (await _service.GetDirectedBuyPageAsync(token, _otherBuyerId, Ct))!.Unavailable.Should().Be("Tickets_Resale_InviteOtherEmail");
    }

    [HumansFact]
    public async Task Pausing_resale_also_stops_the_invite_link()
    {
        var old = await OfferedTicketAsync();
        EmailMessage? sent = null;
        await _email.SendAsync(Arg.Do<EmailMessage>(m => sent = m), Arg.Any<CancellationToken>());
        await _service.InviteBuyersAsync(old.Id, _buyerId, ["bo@example.com"], new Uri("https://h.example/r/"), "en", Ct);
        var token = sent!.HtmlBody["https://h.example/r/".Length..];

        _ticketing.Mode = TicketSalesMode.Open;
        await _service.SetResaleOpenAsync(_ticketing.Id, false, _buyerId, Ct);

        (await _service.StartCheckoutAsync(Buy() with { DirectedToken = token }, _otherBuyerId, Ct))
            .Refusal.Should().Be("Tickets_Resale_NotSelling");
        (await _service.GetDirectedBuyPageAsync(token, _otherBuyerId, Ct))!.Unavailable.Should().Be("Tickets_Resale_NotSelling");
    }

    [HumansFact]
    public async Task Resale_opens_and_pauses_by_hand_and_is_audited()
    {
        _ticketing.ResaleOpenedAt = null;
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        var ticket = _repo.Tickets.Single(t => t.OrderId == order.Id);
        await _service.OfferForResaleAsync(ticket.Id, _buyerId, Ct);
        (await _service.InviteBuyersAsync(ticket.Id, _buyerId, ["bo@example.com"], new Uri("https://h.example/r/"), "en", Ct))
            .Refusal.Should().Be("Tickets_Resale_NotOpenYet");

        (await _service.SetResaleOpenAsync(_ticketing.Id, true, _otherBuyerId, Ct)).Refusal.Should().BeNull();

        _ticketing.ResaleOpenedAt.Should().Be(Now);
        (await _service.InviteBuyersAsync(ticket.Id, _buyerId, ["bo@example.com"], new Uri("https://h.example/r/"), "en", Ct))
            .Refusal.Should().BeNull();
        (await _service.SetResaleOpenAsync(_ticketing.Id, false, _otherBuyerId, Ct)).Refusal.Should().BeNull();
        _ticketing.ResaleOpenedAt.Should().BeNull();
        await _audit.Received(2).LogAsync(TicketsAuditActions.TicketResaleOpenSet, Arg.Any<string>(), _ticketing.Id,
            Arg.Any<string>(), _otherBuyerId);
    }

    [HumansFact]
    public async Task Resale_cannot_open_while_switched_off_and_saving_ticketing_settings_keeps_the_open_state()
    {
        _ticketing.Switches = new TicketingSwitches { Resale = ResaleScheme.Off };
        _ticketing.ResaleOpenedAt = null;
        (await _service.SetResaleOpenAsync(_ticketing.Id, true, _buyerId, Ct)).Refusal
            .Should().Be("Resale is switched off in the ticketing settings.");

        _ticketing.Switches = null;
        await _service.SetResaleOpenAsync(_ticketing.Id, true, _buyerId, Ct);
        var save = new TicketingSettingsSave(_ticketing.Id, _event.Id, "test", null, TicketSalesMode.Open, null, 3000, 0.10m,
            _ticketing.Waves, [], []);
        (await _service.SaveTicketingSettingsAsync(save, _buyerId, Ct)).Refusal.Should().BeNull();

        _repo.Settings.Single().ResaleOpenedAt.Should().Be(Now);

        (await _service.SaveTicketingSettingsAsync(save with { Switches = new TicketingSwitches { Resale = ResaleScheme.Off } }, _buyerId, Ct))
            .Refusal.Should().BeNull();
        _repo.Settings.Single().ResaleOpenedAt.Should().BeNull();
    }

    [HumansFact]
    public async Task Resale_only_mode_cannot_be_paused()
    {
        _ticketing.Mode = TicketSalesMode.ResaleOnly;

        (await _service.SetResaleOpenAsync(_ticketing.Id, false, _buyerId, Ct)).Refusal
            .Should().Be("Resale only mode always sells resale; close sales to stop it.");
        _ticketing.ResaleOpenedAt.Should().NotBeNull();
        await _audit.DidNotReceive().LogAsync(TicketsAuditActions.TicketResaleOpenSet, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>());
    }

    [HumansFact]
    public async Task Invitations_wait_until_resale_opens()
    {
        _ticketing.ResaleOpenedAt = null;
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        var ticket = _repo.Tickets.Single(t => t.OrderId == order.Id);
        await _service.OfferForResaleAsync(ticket.Id, _buyerId, Ct);

        (await _service.InviteBuyersAsync(ticket.Id, _buyerId, ["bo@example.com"], new Uri("https://h.example/r/"), "en", Ct))
            .Refusal.Should().Be("Tickets_Resale_NotOpenYet");
        await _email.DidNotReceiveWithAnyArgs().SendAsync(default!);
    }

    [HumansFact]
    public async Task Wave_pin_keeps_the_wave_on_sale_when_the_grant_was_issued()
    {
        TicketPrice.For(_ticketing, new LocalDate(2027, 2, 10), [], pinnedOn: new LocalDate(2027, 1, 10))!
            .AmountMinor.Should().Be(22500);

        var pinProgram = Guid.NewGuid();
        _ticketing.PriceRules.Add(new TicketPriceRule { GrantProgramId = pinProgram, Kind = TicketPriceRuleKind.WavePin });
        _clock.Reset(Instant.FromUtc(2027, 2, 10, 12, 0));
        _grants.GetActiveGrantsAsync(_buyerId, Arg.Any<CancellationToken>())
            .Returns([new ActiveGrantInfo(pinProgram, _event.Id, Now)]);

        var page = (await _service.GetBuyPagesAsync(_buyerId, Ct)).Single();

        page.OwnPrice!.AmountMinor.Should().Be(22500);
        page.PublicPrice!.AmountMinor.Should().Be(25000);
    }

    [HumansFact]
    public async Task Invited_only_sells_fresh_tickets_to_early_access_holders_with_the_largest_early_amount_off_and_to_nobody_else()
    {
        var invitedProgram = Guid.NewGuid();
        _ticketing.PriceRules.Add(new TicketPriceRule
        {
            GrantProgramId = invitedProgram,
            Kind = TicketPriceRuleKind.EarlyAccess,
            AmountMinor = 2500,
            Label = "Wave zero",
        });
        var secondProgram = Guid.NewGuid();
        _ticketing.PriceRules.Add(new TicketPriceRule
        {
            GrantProgramId = secondProgram,
            Kind = TicketPriceRuleKind.EarlyAccess,
            AmountMinor = 1000,
            Label = "Colaboradores",
        });
        _ticketing.Mode = TicketSalesMode.InvitedOnly;
        _grants.GetActiveGrantsAsync(_buyerId, Arg.Any<CancellationToken>())
            .Returns([
                new ActiveGrantInfo(invitedProgram, _event.Id, Now),
                new ActiveGrantInfo(secondProgram, _event.Id, Now),
            ]);

        (await _service.GetBuyPagesAsync(_otherBuyerId, Ct)).Should().BeEmpty();
        (await _service.GetPublicSalesAsync(Ct)).Should().BeEmpty();
        (await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct)).Refusal.Should().Be("Tickets_Buy_NotOnSale");

        var page = (await _service.GetBuyPagesAsync(_buyerId, Ct)).Single();
        page.OwnPrice!.AmountMinor.Should().Be(20000);
        page.OffersOthers.Should().BeFalse();
        (await _service.StartCheckoutAsync(Buy(others: ["cy@example.com"]), _buyerId, Ct))
            .Refusal.Should().Be("Tickets_Buy_Invalid");
        (await _service.StartCheckoutAsync(Buy(), _buyerId, Ct)).Refusal.Should().BeNull();
        _repo.Orders.Single().TotalMinor.Should().Be(20000);

        _ticketing.Mode = TicketSalesMode.Open;
        (await _service.GetBuyPagesAsync(_buyerId, Ct)).Single().OwnPrice!.AmountMinor.Should().Be(22500);
    }

    [HumansFact]
    public async Task Invited_only_sells_no_resale_and_needs_an_early_access_rule()
    {
        var ticket = await PaidAdultAsync();
        _ticketing.Mode = TicketSalesMode.InvitedOnly;

        _ticketing.IsResaleSelling(Now).Should().BeFalse();
        (await _service.GetHeldTicketsAsync(_buyerId, Ct)).Single(t => t.Ticket.Id == ticket.Id).CanOffer.Should().BeFalse();
        (await _service.OfferForResaleAsync(ticket.Id, _buyerId, Ct)).Refusal.Should().Be("Tickets_Resale_Closed");
        var save = new TicketingSettingsSave(_ticketing.Id, _event.Id, "test", null, TicketSalesMode.InvitedOnly, null, 100, 0.1m,
            [], [], _ticketing.PriceRules);
        (await _service.SaveTicketingSettingsAsync(save, _adminId, Ct)).Refusal
            .Should().Be("Invited only needs an early access price rule naming the grant program that is invited.");
    }

    private readonly Guid _adminId = Guid.NewGuid();

    private async Task<TicketCheckoutTicket> PaidAdultAsync()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        return _repo.Tickets.Single(t => t.OrderId == order.Id && t.Kind == TicketHolderKind.Adult);
    }

    [HumansFact]
    public async Task Issuing_and_renaming_drop_the_new_holders_cached_deletion_hold()
    {
        var newHolder = Guid.NewGuid();
        _holdingsInner.GetUserTicketHoldingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new UserTicketHoldings(0, []));
        await _holdings.GetUserTicketHoldingsAsync(_buyerId, Ct);
        await _holdings.GetUserTicketHoldingsAsync(newHolder, Ct);

        var ticket = await PaidAdultAsync();
        await _holdings.GetUserTicketHoldingsAsync(_buyerId, Ct);
        _provisioning.FindOrCreateUserByEmailAsync("cy@example.com", null, ContactSource.Manual, Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(newHolder, Created: true));
        (await _service.RenameTicketAsync(ticket.Id, "Cy", "cy@example.com", "Agreed by the ticketing lead", false, _adminId, Ct))
            .Refusal.Should().BeNull();
        await _holdings.GetUserTicketHoldingsAsync(newHolder, Ct);

        await _holdingsInner.Received(2).GetUserTicketHoldingsAsync(_buyerId, Arg.Any<CancellationToken>());
        await _holdingsInner.Received(2).GetUserTicketHoldingsAsync(newHolder, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Rename_moves_a_full_price_ticket_to_the_new_holder_and_audits_it()
    {
        var ticket = await PaidAdultAsync();
        var newHolder = Guid.NewGuid();
        _provisioning.FindOrCreateUserByEmailAsync("cy@example.com", null, ContactSource.Manual, Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(newHolder, Created: true));

        (await _service.RenameTicketAsync(ticket.Id, " Cy Pérez ", "cy@example.com", "Agreed by the ticketing lead", false, _adminId, Ct))
            .Refusal.Should().BeNull();

        ticket.HolderUserId.Should().Be(newHolder);
        ticket.HolderName.Should().Be("Cy Pérez");
        ticket.State.Should().Be(TicketCheckoutTicketState.Valid);
        _repo.Events.Should().Contain(e => e.Kind == TicketCheckoutOrderEventKind.Renamed && e.ActorUserId == _adminId
            && !e.Detail.Contains("Cy", StringComparison.Ordinal));
        _repo.Orders.Single(o => o.Id == ticket.OrderId).Lines.Single(l => l.Kind == OrderLineKind.Ticket)
            .HolderUserId.Should().Be(newHolder);
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCheckoutTicketRenamed, Arg.Any<string>(), ticket.OrderId,
            Arg.Any<string>(), _adminId, newHolder, AuditSubjectTypes.User);
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCheckoutTicketRenamed, Arg.Any<string>(), ticket.OrderId,
            Arg.Any<string>(), _adminId, _buyerId, AuditSubjectTypes.User);
        await _delivery.Received(1).TicketRenamedAsync(ticket.Id, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Rename_refuses_an_alias_of_someone_who_already_holds_a_ticket()
    {
        var ticket = await PaidAdultAsync();
        await BuyAndPayAsync(Buy(), _otherBuyerId);
        _userEmails.FindByAddressAsync("bo@example.com", true, true, Arg.Any<CancellationToken>())
            .Returns([Row(_otherBuyerId, verified: true)]);

        (await _service.RenameTicketAsync(ticket.Id, "Bo", "bo@example.com", "Asked nicely", false, _adminId, Ct))
            .Refusal.Should().Be("That person already holds a ticket for this event.");
        ticket.HolderUserId.Should().Be(_buyerId);
    }

    [HumansFact]
    public async Task Rename_to_an_unverified_address_gives_the_ticket_a_new_account_without_a_public_name()
    {
        var ticket = await PaidAdultAsync();
        var row = Row(_otherBuyerId, verified: false);
        var fresh = Guid.NewGuid();
        _userEmails.FindByAddressAsync("bo@example.com", true, false, Arg.Any<CancellationToken>()).Returns([row]);
        _provisioning.ReplaceUnverifiedEmailAsync(_otherBuyerId, row.Id, "bo@example.com", null, ContactSource.Manual,
                Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(fresh, Created: true));

        (await _service.RenameTicketAsync(ticket.Id, "Bo Smith", "bo@example.com", "Asked nicely", false, _adminId, Ct))
            .Refusal.Should().BeNull();
        ticket.HolderUserId.Should().Be(fresh);
        await _provisioning.DidNotReceiveWithAnyArgs().FindOrCreateUserByEmailAsync(default!, default!, default);
    }

    [HumansFact]
    public async Task Rename_needs_a_reason_within_the_limit()
    {
        var ticket = await PaidAdultAsync();

        (await _service.RenameTicketAsync(ticket.Id, "Cy", "cy@example.com", " ", false, _adminId, Ct))
            .Refusal.Should().StartWith("Give a reason");
        (await _service.RenameTicketAsync(ticket.Id, "Cy", "cy@example.com", new string('x', 501), false, _adminId, Ct))
            .Refusal.Should().StartWith("Keep the reason under");
        await _provisioning.DidNotReceiveWithAnyArgs().FindOrCreateUserByEmailAsync(default!, default!, default);
    }

    [HumansFact]
    public async Task Rename_to_another_person_refuses_class_priced_tickets_but_a_name_fix_is_allowed()
    {
        _grants.GetActiveGrantsAsync(_buyerId, Arg.Any<CancellationToken>())
            .Returns([new ActiveGrantInfo(LowIncomeProgram, _event.Id, Now)]);
        var ticket = await PaidAdultAsync();
        _userEmails.FindByAddressAsync("ana@example.com", true, true, Arg.Any<CancellationToken>())
            .Returns([Row(_buyerId, verified: true)]);

        (await _service.RenameTicketAsync(ticket.Id, "Cy", "cy@example.com", "Asked nicely", false, _adminId, Ct))
            .Refusal.Should().StartWith("Only a full price ticket");
        ticket.HolderUserId.Should().Be(_buyerId);
        await _provisioning.DidNotReceiveWithAnyArgs().FindOrCreateUserByEmailAsync(default!, default!, default);

        (await _service.RenameTicketAsync(ticket.Id, "Ana María García", "ana@example.com", "Typo in the legal name", false, _adminId, Ct))
            .Refusal.Should().BeNull();
        ticket.HolderName.Should().Be("Ana María García");
        _repo.Orders.Single(o => o.Id == ticket.OrderId).Lines.Single(l => l.Kind == OrderLineKind.Ticket)
            .HolderName.Should().Be("Ana María García");
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCheckoutTicketRenamed, Arg.Any<string>(), ticket.OrderId,
            Arg.Any<string>(), _adminId, _buyerId, AuditSubjectTypes.User);
    }

    [HumansFact]
    public async Task RenameTicketAsync_MergedHolder_UpdatesExactLine()
    {
        var other = Human("other@example.com", "Other", "Holder");
        var order = await BuyAndPayAsync(Buy(others: ["other@example.com"]), _buyerId);
        var ticket = _repo.Tickets.Single(t => t.HolderUserId == other);
        ticket.HolderUserId = _buyerId;
        order.Lines[ticket.LineIndex].HolderUserId = _buyerId;
        var before = order.Lines[0].HolderName;
        Human("next@example.com", "Next", "Holder");

        (await _service.RenameTicketAsync(ticket.Id, "Next Holder", "next@example.com", "Move", false, _adminId, Ct))
            .Succeeded.Should().BeTrue();

        order.Lines[0].HolderName.Should().Be(before);
        order.Lines[ticket.LineIndex].HolderName.Should().Be("Next Holder");
        order.Lines[ticket.LineIndex].HolderUserId.Should().Be(ticket.HolderUserId);
    }

    [HumansFact]
    public async Task RenameTicketAsync_OrderWriteFails_WritesNoTicket()
    {
        var ticket = await PaidAdultAsync();
        _userEmails.FindByAddressAsync("ana@example.com", true, true, Arg.Any<CancellationToken>())
            .Returns([Row(_buyerId, verified: true)]);
        _repo.RejectOrderWrites = true;
        _repo.TicketWrites.Clear();
        var rename = () => _service.RenameTicketAsync(ticket.Id, "Corrected Name", "ana@example.com", "Typo", false, _adminId, Ct);

        await rename.Should().ThrowAsync<InvalidOperationException>().WithMessage("Order write failed");
        _repo.TicketWrites.Should().BeEmpty();
        await _delivery.DidNotReceive().TicketRenamedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenameTicketAsync_InvalidLine_LeavesHolder(bool missing)
    {
        var ticket = await PaidAdultAsync();
        var order = _repo.Orders.Single(o => o.Id == ticket.OrderId);
        if (missing)
            order.Lines.Clear();
        else
            order.Lines[ticket.LineIndex].Kind = OrderLineKind.Donation;
        _repo.TicketWrites.Clear();

        (await _service.RenameTicketAsync(ticket.Id, "Other Holder", "other@example.com", "Move", false, _adminId, Ct))
            .Succeeded.Should().BeFalse();

        ticket.HolderUserId.Should().Be(_buyerId);
        _repo.TicketWrites.Should().BeEmpty();
        await _provisioning.DidNotReceiveWithAnyArgs().FindOrCreateUserByEmailAsync(default!, default!, default);
    }

    [HumansFact]
    public async Task Void_takes_an_offered_ticket_out_of_the_queue_with_its_dependents()
    {
        _ticketing.ChildTypes = [new TicketChildType { Name = "Child", UnderAge = 13, PriceMinor = 5000 }];
        var adult = await OfferedTicketAsync(Buy() with { Children = [new("Kid García", null)] });
        var child = _repo.Tickets.Single(t => t.Kind == TicketHolderKind.Child);

        (await _service.VoidTicketAsync(adult.Id, "Duplicate order", 0, _adminId, Ct)).Refusal.Should().BeNull();

        adult.State.Should().Be(TicketCheckoutTicketState.Void);
        adult.VoidReason.Should().Be("Duplicate order");
        child.State.Should().Be(TicketCheckoutTicketState.Void);
        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Withdrawn);
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCheckoutTicketVoided, Arg.Any<string>(), adult.OrderId,
            Arg.Is<string>(d => d.Contains("withdrawing its resale offer", StringComparison.Ordinal)), _adminId,
            _buyerId, AuditSubjectTypes.User);
        (await _service.VoidTicketAsync(adult.Id, "Again", 0, _adminId, Ct)).Succeeded.Should().BeFalse();
    }

    [HumansFact]
    public async Task VoidTicketAsync_InterruptedAfterRefund_ReusesOriginalIntent()
    {
        var adult = await PaidAdultAsync();
        _audit.LogAsync(TicketsAuditActions.TicketCheckoutRefunded, Arg.Any<string>(), adult.OrderId,
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<string?>())
            .Returns(_ => throw new InvalidOperationException("Interrupted after refund"));
        var attempt = () => _service.VoidTicketAsync(adult.Id, "Original void", 5000, _adminId, Ct);
        await attempt.Should().ThrowAsync<InvalidOperationException>().WithMessage("Interrupted after refund");
        adult.State.Should().Be(TicketCheckoutTicketState.Valid);
        _repo.Refunds.Should().ContainSingle().Which.Refund.State.Should().Be(RefundState.Succeeded);
        _audit.LogAsync(TicketsAuditActions.TicketCheckoutRefunded, Arg.Any<string>(), adult.OrderId,
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<string?>()).Returns(Task.CompletedTask);

        (await _service.VoidTicketAsync(adult.Id, "Changed retry", 7000, _adminId, Ct)).Succeeded.Should().BeTrue();

        _repo.Refunds.Should().ContainSingle();
        adult.State.Should().Be(TicketCheckoutTicketState.Void);
        adult.VoidReason.Should().Be("Original void");
        await _platform.Received(1).RefundAsync(Arg.Is<RefundRequest>(r => r.AmountMinor == 5000), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task VoidTicketAsync_DependentWriteFails_WritesNoPartialVoid()
    {
        _ticketing.ChildTypes = [new TicketChildType { Name = "Child", UnderAge = 13, PriceMinor = 5000 }];
        var order = await BuyAndPayAsync(Buy() with { Children = [new("Kid García", null)] }, _buyerId);
        var adult = _repo.Tickets.Single(t => t.Kind == TicketHolderKind.Adult);
        var child = _repo.Tickets.Single(t => t.Kind == TicketHolderKind.Child);
        _repo.RejectTicketWrites.Add(child.Id);
        _repo.TicketWrites.Clear();
        var attempt = () => _service.VoidTicketAsync(adult.Id, "Returned", 0, _adminId, Ct);

        await attempt.Should().ThrowAsync<InvalidOperationException>().WithMessage("Ticket write failed");

        _repo.TicketWrites.Should().BeEmpty();
    }

    [HumansFact]
    public async Task VoidTicketAsync_DeclinedRefund_ClearsIntent()
    {
        var adult = await PaidAdultAsync();
        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Failed, "re_declined", "Declined"));

        (await _service.VoidTicketAsync(adult.Id, "Returned", 5000, _adminId, Ct)).Succeeded.Should().BeFalse();

        adult.State.Should().Be(TicketCheckoutTicketState.Valid);
        adult.VoidRefundId.Should().BeNull();
        adult.VoidRefundMinor.Should().BeNull();
        adult.VoidRequestedReason.Should().BeNull();
        (await _service.VoidTicketAsync(adult.Id, "No refund", 0, _adminId, Ct)).Succeeded.Should().BeTrue();
        _repo.Refunds.Should().ContainSingle();
    }

    [HumansFact]
    public async Task VoidTicketAsync_PendingIntent_BlocksHolderMutations()
    {
        var adult = await PaidAdultAsync();
        adult.VoidRefundId = Guid.NewGuid();
        adult.VoidRefundMinor = 5000;
        adult.VoidRequestedReason = "Returned";

        (await _service.RenameTicketAsync(adult.Id, "Other Person", "other@example.com", "Move", false, _adminId, Ct))
            .Succeeded.Should().BeFalse();
        (await _service.OfferForResaleAsync(adult.Id, _buyerId, Ct)).Succeeded.Should().BeFalse();
        adult.State.Should().Be(TicketCheckoutTicketState.Valid);
    }

    [HumansFact]
    public async Task StartCheckoutAsync_PendingVoid_ExcludesResaleOffer()
    {
        var adult = await OfferedTicketAsync();
        adult.VoidRefundId = Guid.NewGuid();
        adult.VoidRefundMinor = 5000;
        adult.VoidRequestedReason = "Returned";
        var buyer = Human("next@example.com", "Next", "Buyer");

        (await _service.StartCheckoutAsync(Buy(), buyer, Ct)).Refusal.Should().Be("Tickets_Buy_ResaleOnly");
        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Open);
    }

    [HumansFact]
    public async Task Void_refuses_while_a_buyer_is_paying_for_the_offer()
    {
        var adult = await OfferedTicketAsync();
        _repo.Offers.Single().State = TicketResaleOfferState.Reserved;

        (await _service.VoidTicketAsync(adult.Id, "Fraud", 0, _adminId, Ct)).Succeeded.Should().BeFalse();
        adult.State.Should().Be(TicketCheckoutTicketState.Offered);
    }

    [HumansFact]
    public async Task Companion_is_a_free_attached_ticket_on_its_own_paid_order_once_per_adult()
    {
        var adult = await PaidAdultAsync();
        _delivery.ClearReceivedCalls();

        (await _service.IssueCompanionAsync(adult.Id, "Helper Ruiz", null, "Wheelchair user", false, _adminId, Ct)).Refusal.Should().BeNull();

        var companion = _repo.Tickets.Single(t => t.Kind == TicketHolderKind.Companion);
        companion.ParentTicketId.Should().Be(adult.Id);
        companion.PricePaidMinor.Should().Be(0);
        companion.HolderUserId.Should().BeNull();
        var order = _repo.Orders.Single(o => o.Id == companion.OrderId);
        order.State.Should().Be(TicketCheckoutOrderState.Paid);
        order.TotalMinor.Should().Be(0);
        await _delivery.Received(1).TicketsIssuedAsync(order.Id, Arg.Any<CancellationToken>());
        order.BuyerUserId.Should().Be(_buyerId);
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCompanionIssued, Arg.Any<string>(), order.Id,
            Arg.Any<string>(), _adminId, _buyerId, AuditSubjectTypes.User);

        (await _service.IssueCompanionAsync(adult.Id, "Second Helper", null, "Again", false, _adminId, Ct)).Succeeded.Should().BeFalse();
    }

    [HumansFact]
    public async Task Void_refuses_a_child_while_its_adult_waits_in_the_queue()
    {
        _ticketing.ChildTypes = [new TicketChildType { Name = "Child", UnderAge = 13, PriceMinor = 5000 }];
        await OfferedTicketAsync(Buy() with { Children = [new("Kid García", null)] });
        var child = _repo.Tickets.Single(t => t.Kind == TicketHolderKind.Child);

        (await _service.VoidTicketAsync(child.Id, "Not coming", 0, _adminId, Ct))
            .Refusal.Should().StartWith("The adult's ticket is in the resale queue");
        child.State.Should().Be(TicketCheckoutTicketState.Valid);
    }

    [HumansFact]
    public async Task A_companion_on_its_own_order_is_voided_when_its_adult_resells()
    {
        var adult = await PaidAdultAsync();
        (await _service.IssueCompanionAsync(adult.Id, "Helper Ruiz", null, "Wheelchair user", false, _adminId, Ct)).Refusal.Should().BeNull();
        var companion = _repo.Tickets.Single(t => t.Kind == TicketHolderKind.Companion);
        (await _service.OfferForResaleAsync(adult.Id, _buyerId, Ct)).Refusal.Should().BeNull();
        _ticketing.Mode = TicketSalesMode.ResaleOnly;

        await BuyAndPayAsync(Buy(), _otherBuyerId);

        adult.State.Should().Be(TicketCheckoutTicketState.Resold);
        companion.State.Should().Be(TicketCheckoutTicketState.Void);
        _repo.Events.Should().Contain(e => e.OrderId == companion.OrderId && e.Kind == TicketCheckoutOrderEventKind.Voided);
    }

    [HumansFact]
    public async Task Companion_refuses_someone_who_already_holds_a_ticket()
    {
        var adult = await PaidAdultAsync();
        _userEmails.FindByAddressAsync("ana@example.com", true, true, Arg.Any<CancellationToken>())
            .Returns([Row(_buyerId, verified: true)]);

        (await _service.IssueCompanionAsync(adult.Id, "Ana", "ana@example.com", "Helper", false, _adminId, Ct))
            .Refusal.Should().Be("That person already holds a ticket for this event.");
    }

    [HumansTheory]
    [InlineData((int)TicketCheckoutOrderState.Pending, true, true, true)]
    [InlineData((int)TicketCheckoutOrderState.Processing, false, true, true)]
    [InlineData((int)TicketCheckoutOrderState.Created, false, false, true)]
    [InlineData((int)TicketCheckoutOrderState.Pending, false, true, false)]
    [InlineData((int)TicketCheckoutOrderState.Cancelled, true, false, false)]
    public async Task IssueCompanionAsync_UnissuedPurchase_RespectsHolderReservation(
        int state, bool remote, bool expired, bool refused)
    {
        var adult = await PaidAdultAsync();
        var companionId = Human("helper@example.com", "Helper", "Ruiz");
        _repo.Orders.Add(new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = _ticketing.Id,
            BuyerUserId = Guid.NewGuid(),
            State = (TicketCheckoutOrderState)state,
            PlatformOrderId = remote ? "remote" : null,
            ExpiresAt = Now + Duration.FromHours(expired ? -1 : 1),
            Lines = [new TicketCheckoutOrderLine
            {
                Kind = OrderLineKind.Ticket,
                HolderKind = TicketHolderKind.Adult,
                HolderUserId = companionId,
            }],
        });
        var ordersBefore = _repo.Orders.Count;
        _delivery.ClearReceivedCalls();

        var result = await _service.IssueCompanionAsync(
            adult.Id, "Helper Ruiz", "helper@example.com", "Support", false, _adminId, Ct);

        result.Succeeded.Should().Be(!refused);
        if (refused)
        {
            result.Refusal.Should().Be("That person already holds a ticket for this event.");
            _repo.Orders.Should().HaveCount(ordersBefore);
            _repo.Tickets.Should().NotContain(t => t.Kind == TicketHolderKind.Companion);
            await _delivery.DidNotReceive().TicketsIssuedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        }
        else
            _repo.Tickets.Should().ContainSingle(t => t.Kind == TicketHolderKind.Companion && t.HolderUserId == companionId);
    }

    [HumansFact]
    public async Task Companion_refuses_an_ambiguous_email()
    {
        var adult = await PaidAdultAsync();
        _userEmails.FindByAddressAsync("bo@example.com", true, true, Arg.Any<CancellationToken>())
            .Returns([Row(Guid.NewGuid(), verified: true), Row(Guid.NewGuid(), verified: true)]);

        (await _service.IssueCompanionAsync(adult.Id, "Bo", "bo@example.com", "Helper", false, _adminId, Ct))
            .Refusal.Should().StartWith("That email cannot take the ticket: the address is verified on more than one account");
        _repo.Tickets.Should().NotContain(t => t.Kind == TicketHolderKind.Companion);
    }

    [HumansFact]
    public async Task Companion_respects_capacity()
    {
        var adult = await PaidAdultAsync();
        _ticketing.Capacity = 1;

        (await _service.IssueCompanionAsync(adult.Id, "Helper Ruiz", null, "Wheelchair user", false, _adminId, Ct))
            .Refusal.Should().Contain("capacity");
        _repo.Tickets.Should().ContainSingle();
    }

    [HumansFact]
    public async Task No_companion_requests_when_the_event_sells_no_companion_tickets()
    {
        var adult = await PaidAdultAsync();
        Human("helper@example.com", "Helper", "Ruiz");
        _ticketing.Switches = new TicketingSwitches { CompanionTickets = false };

        (await _service.RequestCompanionAsync(adult.Id, _buyerId, "helper@example.com", "Wheelchair user", Ct))
            .Refusal.Should().Be("Tickets_Companion_NotAllowed");
        _repo.CompanionRequests.Should().BeEmpty();
    }

    [HumansFact]
    public async Task A_holder_asks_for_a_companion_in_Humans_and_approval_issues_it_to_their_account()
    {
        var adult = await PaidAdultAsync();
        var helper = Human("helper@example.com", "Helper", "Ruiz");
        _delivery.ClearReceivedCalls();

        (await _service.RequestCompanionAsync(adult.Id, _buyerId, " HELPER@example.com ", " Wheelchair user ", Ct))
            .Refusal.Should().BeNull();
        (await _service.RequestCompanionAsync(adult.Id, _buyerId, "helper@example.com", "Again", Ct))
            .Refusal.Should().Be("Tickets_Companion_NotAllowed");
        var request = _repo.CompanionRequests.Single();
        request.State.Should().Be(TicketCompanionRequestState.Pending);
        request.CompanionUserId.Should().Be(helper);
        request.Note.Should().Be("Wheelchair user");
        _repo.Events.Should().Contain(e => e.Kind == TicketCheckoutOrderEventKind.CompanionRequested)
            .And.NotContain(e => e.Detail.Contains("Wheelchair"));
        _repo.Tickets.Should().NotContain(t => t.Kind == TicketHolderKind.Companion);

        (await _service.DecideCompanionRequestAsync(request.Id, approve: true, null, _adminId, Ct)).Refusal.Should().BeNull();

        var companion = _repo.Tickets.Single(t => t.Kind == TicketHolderKind.Companion);
        companion.HolderUserId.Should().Be(helper);
        companion.HolderName.Should().Be("Helper Ruiz");
        companion.ParentTicketId.Should().Be(adult.Id);
        companion.PricePaidMinor.Should().Be(0);
        request.State.Should().Be(TicketCompanionRequestState.Approved);
        request.CompanionTicketId.Should().Be(companion.Id);
        request.DecidedByUserId.Should().Be(_adminId);
        await _provisioning.DidNotReceiveWithAnyArgs().FindOrCreateUserByEmailAsync(default!, default, default);
        await _delivery.Received(1).TicketsIssuedAsync(companion.OrderId, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCompanionRequestDecided, Arg.Any<string>(), request.Id,
            Arg.Any<string>(), _adminId, _buyerId, AuditSubjectTypes.User);
        (await _service.RequestCompanionAsync(adult.Id, _buyerId, "helper@example.com", "A second one", Ct))
            .Refusal.Should().Be("Tickets_Companion_NotAllowed");
        (await _service.StartCheckoutAsync(Buy(forSelf: false, others: ["helper@example.com"]), _otherBuyerId, Ct))
            .Refusal.Should().Be("Tickets_Buy_OtherHolds");
    }

    [HumansFact]
    public async Task DecideCompanionRequestAsync_IssuedBeforeInterruption_CompletesRequest()
    {
        var adult = await PaidAdultAsync();
        var helper = Human("helper@example.com", "Helper", "Ruiz");
        (await _service.RequestCompanionAsync(adult.Id, _buyerId, "helper@example.com", "Support", Ct)).Succeeded.Should().BeTrue();
        var request = _repo.CompanionRequests.Single();
        (await _service.IssueCompanionAsync(adult.Id, "Helper Ruiz", "helper@example.com", "Interrupted approval", false, _adminId, Ct))
            .Succeeded.Should().BeTrue();
        var companion = _repo.Tickets.Single(t => t.Kind == TicketHolderKind.Companion);
        companion.HolderUserId.Should().Be(helper);
        var orders = _repo.Orders.Count;
        var tickets = _repo.Tickets.Count;
        _delivery.ClearReceivedCalls();

        var result = await _service.DecideCompanionRequestAsync(request.Id, true, null, _adminId, Ct);

        result.Succeeded.Should().BeTrue();
        request.State.Should().Be(TicketCompanionRequestState.Approved);
        request.CompanionTicketId.Should().Be(companion.Id);
        _repo.Orders.Should().HaveCount(orders);
        _repo.Tickets.Should().HaveCount(tickets);
        await _delivery.DidNotReceiveWithAnyArgs().TicketsIssuedAsync(default);
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCompanionRequestDecided, Arg.Any<string>(), request.Id,
            Arg.Any<string>(), _adminId, _buyerId, AuditSubjectTypes.User);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetHeldTickets_RenamedParent_HidesPreviousRequest(bool declined)
    {
        var adult = await PaidAdultAsync();
        Human("helper@example.com", "Helper", "Ruiz");
        var nextHolder = Human("next@example.com", "Next", "Holder");
        await _service.RequestCompanionAsync(adult.Id, _buyerId, "helper@example.com", "Private support needs", Ct);
        var request = _repo.CompanionRequests.Single();
        if (declined)
            await _service.DecideCompanionRequestAsync(request.Id, false, "Private decision", _adminId, Ct);
        (await _service.RenameTicketAsync(adult.Id, "Next Holder", "next@example.com", "Transfer requested", false, _adminId, Ct))
            .Succeeded.Should().BeTrue();

        var held = (await _service.GetHeldTicketsAsync(nextHolder, Ct)).Single(t => t.Ticket.Id == adult.Id);

        held.CompanionRequest.Should().BeNull();
        held.CanAskCompanion.Should().BeTrue();
        _repo.CompanionRequests.Should().ContainSingle(r => r.Id == request.Id && r.HolderUserId == _buyerId);
    }

    [HumansFact]
    public async Task WithdrawCompanionRequest_RenamedParent_PreservesPreviousRequest()
    {
        var adult = await PaidAdultAsync();
        Human("helper@example.com", "Helper", "Ruiz");
        var nextHolder = Human("next@example.com", "Next", "Holder");
        await _service.RequestCompanionAsync(adult.Id, _buyerId, "helper@example.com", "Original request", Ct);
        var previous = _repo.CompanionRequests.Single();
        (await _service.RenameTicketAsync(adult.Id, "Next Holder", "next@example.com", "Transfer requested", false, _adminId, Ct))
            .Succeeded.Should().BeTrue();

        (await _service.WithdrawCompanionRequestAsync(adult.Id, nextHolder, Ct))
            .Refusal.Should().Be("Tickets_Companion_NothingPending");
        (await _service.RequestCompanionAsync(adult.Id, nextHolder, "helper@example.com", "New request", Ct))
            .Succeeded.Should().BeTrue();
        (await _service.WithdrawCompanionRequestAsync(adult.Id, nextHolder, Ct)).Succeeded.Should().BeTrue();

        previous.State.Should().Be(TicketCompanionRequestState.Pending);
        _repo.CompanionRequests.Should().ContainSingle(r => r.HolderUserId == nextHolder && r.State == TicketCompanionRequestState.Withdrawn);
    }

    [HumansFact]
    public async Task GetCompanionRequests_RenamedParent_LabelsOriginalHolder()
    {
        var adult = await PaidAdultAsync();
        var original = (await _users.GetUserInfoAsync(_buyerId, Ct))!.FullName;
        Human("helper@example.com", "Helper", "Ruiz");
        Human("next@example.com", "Next", "Holder");
        await _service.RequestCompanionAsync(adult.Id, _buyerId, "helper@example.com", "Original request", Ct);
        (await _service.RenameTicketAsync(adult.Id, "Next Holder", "next@example.com", "Transfer requested", false, _adminId, Ct))
            .Succeeded.Should().BeTrue();

        var row = (await _service.GetCompanionRequestsAsync(_ticketing.Id, Ct)).Single();

        row.HolderName.Should().Be(original);
        row.Request.HolderUserId.Should().Be(_buyerId);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecideCompanionRequest_RenamedParent_RefusesApproval(bool alreadyIssued)
    {
        var adult = await PaidAdultAsync();
        Human("helper@example.com", "Helper", "Ruiz");
        Human("next@example.com", "Next", "Holder");
        await _service.RequestCompanionAsync(adult.Id, _buyerId, "helper@example.com", "Original request", Ct);
        var request = _repo.CompanionRequests.Single();
        (await _service.RenameTicketAsync(adult.Id, "Next Holder", "next@example.com", "Transfer requested", false, _adminId, Ct))
            .Succeeded.Should().BeTrue();
        if (alreadyIssued)
            (await _service.IssueCompanionAsync(adult.Id, "Helper Ruiz", "helper@example.com", "New holder's companion", false, _adminId, Ct))
                .Succeeded.Should().BeTrue();
        var orders = _repo.Orders.Count;
        var tickets = _repo.Tickets.Count;
        _delivery.ClearReceivedCalls();

        var result = await _service.DecideCompanionRequestAsync(request.Id, true, null, _adminId, Ct);

        result.Succeeded.Should().BeFalse();
        request.State.Should().Be(TicketCompanionRequestState.Pending);
        request.CompanionTicketId.Should().BeNull();
        _repo.Orders.Should().HaveCount(orders);
        _repo.Tickets.Should().HaveCount(tickets);
        await _delivery.DidNotReceiveWithAnyArgs().TicketsIssuedAsync(default);
        await _audit.DidNotReceive().LogAsync(TicketsAuditActions.TicketCompanionRequestDecided, Arg.Any<string>(), request.Id,
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
        (await _service.DecideCompanionRequestAsync(request.Id, false, "Ticket transferred", _adminId, Ct))
            .Succeeded.Should().BeTrue();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecideCompanionRequestAsync_DifferentCompanion_Refuses(bool differentParent)
    {
        var adult = await PaidAdultAsync();
        Human("helper@example.com", "Helper", "Ruiz");
        Human("another@example.com", "Another", "Helper");
        (await _service.RequestCompanionAsync(adult.Id, _buyerId, "helper@example.com", "Support", Ct)).Succeeded.Should().BeTrue();
        var request = _repo.CompanionRequests.Single();
        var parent = adult;
        if (differentParent)
        {
            var otherOrder = await BuyAndPayAsync(Buy(), _otherBuyerId);
            parent = _repo.Tickets.Single(t => t.OrderId == otherOrder.Id);
        }
        var address = differentParent ? "helper@example.com" : "another@example.com";
        (await _service.IssueCompanionAsync(parent.Id, "Companion", address, "Another approval", false, _adminId, Ct))
            .Succeeded.Should().BeTrue();
        var tickets = _repo.Tickets.Count;

        var result = await _service.DecideCompanionRequestAsync(request.Id, true, null, _adminId, Ct);

        result.Succeeded.Should().BeFalse();
        request.State.Should().Be(TicketCompanionRequestState.Pending);
        request.CompanionTicketId.Should().BeNull();
        _repo.Tickets.Should().HaveCount(tickets);
    }

    [HumansFact]
    public async Task IssueCompanionAsync_TicketSaveFails_LeavesNoOrder()
    {
        var adult = await PaidAdultAsync();
        var orders = _repo.Orders.Count;
        _repo.RejectCompanionWrites = true;
        var issue = () => _service.IssueCompanionAsync(adult.Id, "Helper Ruiz", null, "Support", false, _adminId, Ct);

        await issue.Should().ThrowAsync<InvalidOperationException>().WithMessage("Companion write failed");

        _repo.Orders.Should().HaveCount(orders);
        _repo.Tickets.Should().ContainSingle().Which.Id.Should().Be(adult.Id);
    }

    [HumansFact]
    public async Task A_companion_request_is_refused_for_someone_elses_ticket_without_a_note_or_without_a_live_account()
    {
        var adult = await PaidAdultAsync();
        Human("helper@example.com", "Helper", "Ruiz");
        Human("noname@example.com", "", "", u => { u.FirstName = ""; u.LastName = ""; });
        _userEmails.FindByAddressAsync("ana@example.com", true, true, Arg.Any<CancellationToken>()).Returns([Row(_buyerId, verified: true)]);

        (await _service.RequestCompanionAsync(adult.Id, _otherBuyerId, "helper@example.com", "Help", Ct))
            .Refusal.Should().Be("Tickets_Resale_NotFound");
        (await _service.RequestCompanionAsync(adult.Id, _buyerId, "helper@example.com", "  ", Ct))
            .Refusal.Should().Be("Tickets_Companion_NoteRequired");
        (await _service.RequestCompanionAsync(adult.Id, _buyerId, "nobody@example.com", "Help", Ct))
            .Refusal.Should().Be("Tickets_Companion_NotFound");
        (await _service.RequestCompanionAsync(adult.Id, _buyerId, "ana@example.com", "Help", Ct))
            .Refusal.Should().Be("Tickets_Companion_IsYou");
        (await _service.RequestCompanionAsync(adult.Id, _buyerId, "noname@example.com", "Help", Ct))
            .Refusal.Should().Be("Tickets_Companion_NeedsName");
        _repo.CompanionRequests.Should().BeEmpty();
    }

    [HumansFact]
    public async Task A_decline_needs_a_note_the_holder_sees_and_lets_them_ask_again()
    {
        var adult = await PaidAdultAsync();
        Human("helper@example.com", "Helper", "Ruiz");
        await _service.RequestCompanionAsync(adult.Id, _buyerId, "helper@example.com", "Help", Ct);
        var request = _repo.CompanionRequests.Single();

        (await _service.DecideCompanionRequestAsync(request.Id, approve: false, " ", _adminId, Ct)).Succeeded.Should().BeFalse();
        (await _service.DecideCompanionRequestAsync(request.Id, approve: false, "Not eligible", _adminId, Ct)).Refusal.Should().BeNull();
        (await _service.DecideCompanionRequestAsync(request.Id, approve: true, null, _adminId, Ct)).Succeeded.Should().BeFalse();

        request.State.Should().Be(TicketCompanionRequestState.Declined);
        request.DecisionNote.Should().Be("Not eligible");
        _repo.Tickets.Should().NotContain(t => t.Kind == TicketHolderKind.Companion);
        var held = (await _service.GetHeldTicketsAsync(_buyerId, Ct)).Single(t => t.Ticket.Id == adult.Id);
        held.CompanionRequest!.DecisionNote.Should().Be("Not eligible");
        held.CanAskCompanion.Should().BeTrue();
    }

    [HumansFact]
    public async Task Approval_rechecks_the_companion_and_a_withdrawn_request_cannot_be_approved()
    {
        var adult = await PaidAdultAsync();
        var helper = Human("helper@example.com", "Helper", "Ruiz");
        await _service.RequestCompanionAsync(adult.Id, _buyerId, "helper@example.com", "Help", Ct);
        var request = _repo.CompanionRequests.Single();
        await BuyAndPayAsync(Buy(), helper);

        (await _service.DecideCompanionRequestAsync(request.Id, approve: true, null, _adminId, Ct))
            .Refusal.Should().StartWith("The companion now holds a ticket");
        request.State.Should().Be(TicketCompanionRequestState.Pending);

        (await _service.WithdrawCompanionRequestAsync(adult.Id, _buyerId, Ct)).Refusal.Should().BeNull();
        (await _service.WithdrawCompanionRequestAsync(adult.Id, _buyerId, Ct)).Refusal.Should().Be("Tickets_Companion_NothingPending");
        (await _service.DecideCompanionRequestAsync(request.Id, approve: false, "Late", _adminId, Ct)).Succeeded.Should().BeFalse();
        request.State.Should().Be(TicketCompanionRequestState.Withdrawn);
    }

    [HumansFact]
    public async Task Erasure_withdraws_a_pending_companion_request_and_the_companions_export_hides_the_note()
    {
        var adult = await PaidAdultAsync();
        var helper = Human("helper@example.com", "Helper", "Ruiz");
        await _service.RequestCompanionAsync(adult.Id, _buyerId, "helper@example.com", "Uses a wheelchair", Ct);

        async Task<string> ExportAsync(Guid userId) => System.Text.Json.JsonSerializer.Serialize(
            (await _service.ContributeForUserAsync(userId, Ct)).Select(slice => slice.Data));
        (await ExportAsync(helper)).Should().Contain(_repo.CompanionRequests.Single().Id.ToString()).And.NotContain("wheelchair");
        (await ExportAsync(_buyerId)).Should().Contain("Uses a wheelchair");

        await _service.EraseForUserAsync(helper, Ct);

        _repo.CompanionRequests.Single().State.Should().Be(TicketCompanionRequestState.Withdrawn);
    }

    [HumansFact]
    public async Task A_lapsed_hold_is_closed_at_the_platform_and_a_late_payment_still_issues_flagged_tickets()
    {
        var offerTicket = await OfferedTicketAsync();
        var started = await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct);
        var order = _repo.Orders.Single(o => o.Id == started.OrderId);
        _platform.GetStatusAsync(order.PlatformOrderId!, Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Pending, null, null, null));

        _clock.Advance(Duration.FromHours(2));
        await _service.ReconcileOpenOrdersAsync(Ct);

        await _platform.Received(1).CancelAsync(order.PlatformOrderId!, Arg.Any<CancellationToken>());
        order.State.Should().Be(TicketCheckoutOrderState.Expired);
        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Open);

        _platform.GetStatusAsync(order.PlatformOrderId!, Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 100, "ideal", _clock.GetCurrentInstant()));
        await _service.PlatformOrderChangedAsync("test", order.PlatformOrderId!, Ct);

        order.State.Should().Be(TicketCheckoutOrderState.Paid);
        _repo.Tickets.Should().Contain(t => t.OrderId == order.Id && t.HolderUserId == _otherBuyerId);
        offerTicket.State.Should().Be(TicketCheckoutTicketState.Resold);
        _repo.Events.Should().Contain(e => e.OrderId == order.Id && e.Detail.Contains("after the order was Expired"));
    }

    [HumansTheory]
    [InlineData((int)TicketHolderKind.Adult)]
    [InlineData((int)TicketHolderKind.Child)]
    [InlineData((int)TicketHolderKind.Companion)]
    public async Task PlatformOrderChangedAsync_AdmittedResale_IssuesFreshStock(int kind)
    {
        var admittedKind = (TicketHolderKind)kind;
        var old = await OfferedTicketAsync();
        var admitted = admittedKind == TicketHolderKind.Adult ? old : AddTicket(admittedKind, parent: old.Id);
        var started = await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct);
        var order = _repo.Orders.Single(o => o.Id == started.OrderId);
        _platform.GetStatusAsync(order.PlatformOrderId!, Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Expired, null, null, null));
        await _service.PlatformOrderChangedAsync("test", order.PlatformOrderId!, Ct);
        var offer = _repo.Offers.Single();
        offer.State.Should().Be(TicketResaleOfferState.Open);
        _ticketing.Mode = TicketSalesMode.Closed;
        (await _service.FindIssuedTicketAsync(admitted.Barcode, Ct))!.IsValid.Should().BeTrue();
        _admitted.Add(admitted.Barcode);

        await PayAsync(order.PlatformOrderId!);
        await _service.ReconcileOpenOrdersAsync(Ct);
        _clock.Advance(TicketCheckoutService.ResaleRefundHold);
        await _service.RunResaleRefundBatchAsync(Ct);

        old.State.Should().Be(TicketCheckoutTicketState.Valid);
        admitted.State.Should().Be(TicketCheckoutTicketState.Valid);
        offer.State.Should().Be(TicketResaleOfferState.Withdrawn);
        offer.ReservedByOrderId.Should().BeNull();
        offer.SoldToOrderId.Should().BeNull();
        offer.RefundId.Should().BeNull();
        _repo.Tickets.Should().ContainSingle(t => t.OrderId == order.Id
            && t.HolderUserId == _otherBuyerId && t.State == TicketCheckoutTicketState.Valid);
        await _platform.DidNotReceive().RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
        await _delivery.DidNotReceive().ResaleSoldAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCheckoutNeedsAttention,
            Arg.Any<string>(), order.Id, Arg.Is<string>(detail => detail.Contains("already admitted", StringComparison.Ordinal)),
            Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<string?>());
        _repo.Events.Should().Contain(e => e.OrderId == old.OrderId
            && e.Kind == TicketCheckoutOrderEventKind.ResaleWithdrawn);
    }

    [HumansFact]
    public async Task A_late_payment_whose_offer_went_to_another_buyer_leaves_that_sale_alone()
    {
        var offerTicket = await OfferedTicketAsync();
        var first = await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct);
        var late = _repo.Orders.Single(o => o.Id == first.OrderId);
        _platform.GetStatusAsync(late.PlatformOrderId!, Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Expired, null, null, null));
        await _service.PlatformOrderChangedAsync("test", late.PlatformOrderId!, Ct);
        var thirdId = Guid.NewGuid();
        var third = new User { Id = thirdId, BurnerName = "Kite", FirstName = "Cy", LastName = "Doe", Email = "cy@example.com" };
        _users.GetUserInfoAsync(thirdId, Arg.Any<CancellationToken>()).Returns(UserInfo.Create(third, [], [], UserFixtures.Profile()));
        var winner = await _service.StartCheckoutAsync(Buy(), thirdId, Ct);
        _repo.Offers.Single().ReservedByOrderId.Should().Be(winner.OrderId);

        _platform.GetStatusAsync(late.PlatformOrderId!, Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, null, "card", Now));
        await _service.PlatformOrderChangedAsync("test", late.PlatformOrderId!, Ct);

        late.State.Should().Be(TicketCheckoutOrderState.Paid);
        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Reserved);
        offerTicket.State.Should().Be(TicketCheckoutTicketState.Offered);
        await _platform.DidNotReceive().RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefundAsync_CancellationDuringAttempt_FinishesRefund(bool replay)
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        using var caller = new CancellationTokenSource();
        if (replay)
        {
            foreach (var id in new[] { "re_first", "re_second" })
            {
                var refund = new TicketCheckoutRefund
                {
                    Id = Guid.NewGuid(),
                    OrderId = order.Id,
                    AmountMinor = 5000,
                    Reason = "Returned",
                    State = RefundState.Pending,
                    PlatformRefundId = id,
                    CreatedAt = Now
                };
                _repo.Refunds.Add((refund.Id, refund));
            }
        }
        _repo.Events.Clear();
        _platform.ClearReceivedCalls();
        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            caller.Cancel();
            call.ArgAt<CancellationToken>(1).ThrowIfCancellationRequested();
            return new RefundResult(RefundState.Succeeded, "re_first", null);
        });

        if (replay)
        {
            var refresh = () => _service.PlatformOrderChangedAsync("test", order.PlatformOrderId!, caller.Token);
            await refresh.Should().ThrowAsync<OperationCanceledException>();
            _repo.Refunds[1].Refund.State.Should().Be(RefundState.Pending);
            await _platform.Received(1).GetStatusAsync(order.PlatformOrderId!, caller.Token);
        }
        else
        {
            (await _service.RefundAsync(order.Id, 5000, "Returned", _buyerId, caller.Token)).Succeeded.Should().BeTrue();
            await _platform.Received(1).GetStatusAsync(order.PlatformOrderId!, CancellationToken.None);
        }

        _repo.Refunds[0].Refund.State.Should().Be(RefundState.Succeeded);
        _repo.Events.Should().Contain(e => e.Kind == TicketCheckoutOrderEventKind.RefundSettled);
        await _platform.Received(1).RefundAsync(Arg.Any<RefundRequest>(), CancellationToken.None);
        _platform.ReceivedCalls().Count(call => string.Equals(call.GetMethodInfo().Name,
            nameof(ITicketingPlatform.RefundAsync), StringComparison.Ordinal)).Should().Be(1);
    }

    [HumansFact]
    public async Task Pending_refunds_take_the_state_the_platform_answers_for_their_id_and_a_failure_frees_the_amount()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Pending, "re_p", null));
        await _service.RefundAsync(order.Id, 10000, "first", Guid.NewGuid(), Ct);
        await _service.RefundAsync(order.Id, 5000, "second", Guid.NewGuid(), Ct);
        _repo.Refunds.Select(r => r.Refund.State).Should().AllBeEquivalentTo(RefundState.Pending);

        _platform.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 0, "card", Now, RefundedMinor: 10000, RefundFailed: true));
        _platform.RefundAsync(Arg.Is<RefundRequest>(r => r.AmountMinor == 10000), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Succeeded, "re_p", null));
        _platform.RefundAsync(Arg.Is<RefundRequest>(r => r.AmountMinor == 5000), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Failed, "re_p", "bank refused"));
        _clock.Advance(Duration.FromSeconds(1));
        await _service.ReconcileOpenOrdersAsync(Ct);

        _repo.Refunds.Select(r => r.Refund.State).Should().Equal(RefundState.Succeeded, RefundState.Failed);
        (await _service.GetOrderDetailAsync(order.Id, Ct))!.RefundableMinor.Should().Be(12500);
        _platform.ReceivedCalls().Where(call => string.Equals(call.GetMethodInfo().Name,
                nameof(ITicketingPlatform.RefundAsync), StringComparison.Ordinal))
            .Select(call => (RefundRequest)call.GetArguments()[0]!).Should().OnlyContain(r => r.CreatedAt == Now);
    }

    [HumansFact]
    public async Task A_refund_made_in_the_platforms_dashboard_does_not_settle_a_pending_refund_of_ours()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Pending, "re_ours", null));
        await _service.RefundAsync(order.Id, 5000, "ours", Guid.NewGuid(), Ct);

        _platform.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 0, "card", Now, RefundedMinor: 5000));
        await _service.ReconcileOpenOrdersAsync(Ct);

        _repo.Refunds.Single().Refund.State.Should().Be(RefundState.Pending);
    }

    [HumansFact]
    public async Task A_refund_whose_call_got_no_answer_is_never_settled_by_a_dashboard_refund()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        _platform.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 0, "card", Now, RefundedMinor: 5000));
        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns<RefundResult>(_ => throw new TicketingPlatformException("timeout"));

        await _service.RefundAsync(order.Id, 5000, "ours", Guid.NewGuid(), Ct);
        await _service.RefreshOrderAsync(order.Id, Ct);

        _repo.Refunds.Single().Refund.State.Should().Be(RefundState.Pending);
    }

    public enum RefundOutcome { Succeeded, Failed, Pending, Unavailable, Unsent }

    [HumansTheory]
    [InlineData(true, RefundOutcome.Succeeded, 5000, RefundState.ManualRequired)]
    [InlineData(false, RefundOutcome.Succeeded, 5000, RefundState.ManualRequired)]
    [InlineData(true, RefundOutcome.Pending, 5000, RefundState.ManualRequired)]
    [InlineData(false, RefundOutcome.Pending, 5000, RefundState.ManualRequired)]
    [InlineData(true, RefundOutcome.Failed, 5000, RefundState.Succeeded)]
    [InlineData(false, RefundOutcome.Failed, 5000, RefundState.Succeeded)]
    [InlineData(true, RefundOutcome.Unavailable, 5000, RefundState.ManualRequired)]
    [InlineData(false, RefundOutcome.Unavailable, 5000, RefundState.ManualRequired)]
    [InlineData(true, RefundOutcome.Unsent, 5000, RefundState.ManualRequired)]
    [InlineData(false, RefundOutcome.Unsent, 5000, RefundState.ManualRequired)]
    [InlineData(true, RefundOutcome.Pending, 10000, RefundState.Succeeded)]
    [InlineData(false, RefundOutcome.Pending, 10000, RefundState.Succeeded)]
    public async Task PlatformOrderChangedAsync_MixedRefunds_AllocatesOnce(
        bool manualFirst, RefundOutcome outcome, long refundedMinor, RefundState manualState)
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        var manual = new TicketCheckoutRefund
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            AmountMinor = 5000,
            Reason = "Manual",
            State = RefundState.ManualRequired,
            CreatedAt = Now + Duration.FromMinutes(manualFirst ? 0 : 1),
        };
        var automated = new TicketCheckoutRefund
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            AmountMinor = 5000,
            Reason = "Automated",
            State = RefundState.Pending,
            PlatformRefundId = outcome == RefundOutcome.Unsent ? null : "re_ours",
            CreatedAt = Now + Duration.FromMinutes(manualFirst ? 1 : 0),
        };
        _repo.Refunds.Add((manual.Id, manual));
        _repo.Refunds.Add((automated.Id, automated));
        _platform.GetStatusAsync(order.PlatformOrderId!, Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 0, "card", Now, RefundedMinor: refundedMinor));
        _platform.RefundAsync(Arg.Is<RefundRequest>(r => r.RefundId == automated.Id), Arg.Any<CancellationToken>())
            .Returns<RefundResult>(_ => outcome switch
            {
                RefundOutcome.Succeeded => new(RefundState.Succeeded, "re_ours", null),
                RefundOutcome.Failed => new(RefundState.Failed, "re_ours", "Declined"),
                RefundOutcome.Pending => new(RefundState.Pending, "re_ours", null),
                _ => throw new TicketingPlatformException("Outcome unavailable"),
            });

        await _service.PlatformOrderChangedAsync("test", order.PlatformOrderId!, Ct);

        manual.State.Should().Be(manualState);
        automated.State.Should().Be(outcome switch
        {
            RefundOutcome.Succeeded => RefundState.Succeeded,
            RefundOutcome.Failed => RefundState.Failed,
            _ => RefundState.Pending,
        });
        _repo.Refunds.Where(r => r.Refund.State == RefundState.Succeeded).Sum(r => r.Refund.AmountMinor)
            .Should().BeLessThanOrEqualTo(refundedMinor);
    }

    [HumansFact]
    public async Task PlatformOrderChangedAsync_ManualRefunds_SettlesOldestFirst()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        var older = new TicketCheckoutRefund
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            AmountMinor = 3000,
            Reason = "Older",
            State = RefundState.ManualRequired,
            CreatedAt = Now,
        };
        var newer = new TicketCheckoutRefund
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            AmountMinor = 2000,
            Reason = "Newer",
            State = RefundState.ManualRequired,
            CreatedAt = Now + Duration.FromMinutes(1),
        };
        _repo.Refunds.Add((newer.Id, newer));
        _repo.Refunds.Add((older.Id, older));
        _platform.GetStatusAsync(order.PlatformOrderId!, Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 0, "card", Now, RefundedMinor: 3000));

        await _service.PlatformOrderChangedAsync("test", order.PlatformOrderId!, Ct);

        older.State.Should().Be(RefundState.Succeeded);
        newer.State.Should().Be(RefundState.ManualRequired);
        await _platform.DidNotReceive().RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task A_chargeback_lowers_what_can_be_refunded_and_keeps_the_ticket_out_of_resale()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        _platform.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 0, "card", Now, ChargedBackMinor: 25000));
        await _service.RefreshOrderAsync(order.Id, Ct);
        var ticket = _repo.Tickets.Single(t => t.OrderId == order.Id);

        (await _service.GetOrderDetailAsync(order.Id, Ct))!.RefundableMinor.Should().Be(0);
        (await _service.RefundAsync(order.Id, 100, "returned", Guid.NewGuid(), Ct)).Refusal.Should().Contain("chargeback");
        (await _service.OfferForResaleAsync(ticket.Id, _buyerId, Ct)).Refusal.Should().Be("Tickets_Resale_NotOfferable");
        _repo.Offers.Should().BeEmpty();
    }

    [HumansFact]
    public async Task A_payment_still_on_its_way_after_two_weeks_goes_to_the_team_once()
    {
        var started = await _service.StartCheckoutAsync(Buy(), _buyerId, Ct);
        _platform.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Processing, null, null, null));
        await _service.ReconcileOpenOrdersAsync(Ct);
        _clock.Advance(Duration.FromDays(15));

        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);

        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCheckoutNeedsAttention, Arg.Any<string>(),
            started.OrderId!.Value, Arg.Is<string>(d => d.StartsWith("Payment on its way", StringComparison.Ordinal)), Arg.Any<string>());
    }

    [HumansFact]
    public async Task A_payment_on_its_way_holds_the_ticket_past_expiry_and_cannot_be_cancelled_until_it_settles()
    {
        var started = await _service.StartCheckoutAsync(Buy(), _buyerId, Ct);
        _platform.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Processing, null, null, null));
        _clock.Advance(Duration.FromDays(2));

        await _service.ReconcileOpenOrdersAsync(Ct);

        var order = _repo.Orders.Single();
        order.State.Should().Be(TicketCheckoutOrderState.Processing);
        await _platform.DidNotReceive().CancelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        var page = (await _service.GetBuyPagesAsync(_buyerId, Ct)).Single();
        page.AlreadyHoldsTicket.Should().BeTrue();
        page.OpenOrder.Should().BeNull();
        (await _service.CancelAsync(started.OrderId!.Value, _buyerId, Ct)).Refusal.Should().Be("Tickets_Buy_PaymentProcessing");
        _audit.ReceivedCalls().Should().NotContain(c => Equals(c.GetArguments()[0], TicketsAuditActions.TicketCheckoutClosed));

        _platform.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 30, "sepa_debit", Now + Duration.FromDays(2)));
        await _service.ReconcileOpenOrdersAsync(Ct);
        order.State.Should().Be(TicketCheckoutOrderState.Paid);
        _repo.Tickets.Should().ContainSingle();
    }

    [HumansFact]
    public async Task A_failed_bank_debit_after_processing_releases_the_hold()
    {
        await _service.StartCheckoutAsync(Buy(), _buyerId, Ct);
        _platform.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Processing, null, null, null));
        await _service.ReconcileOpenOrdersAsync(Ct);
        _platform.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Failed, null, null, null));

        await _service.ReconcileOpenOrdersAsync(Ct);

        _repo.Orders.Single().State.Should().Be(TicketCheckoutOrderState.Failed);
        (await _service.GetBuyPagesAsync(_buyerId, Ct)).Single().AlreadyHoldsTicket.Should().BeFalse();
    }

    [HumansFact]
    public async Task A_chargeback_goes_to_the_team_once_and_again_when_it_closes()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        _platform.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 0, "card", Now, ChargedBackMinor: 25000));

        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.RefreshOrderAsync(order.Id, Ct);
        order.ChargedBackMinor.Should().Be(25000);
        _platform.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 0, "card", Now));
        await _service.RefreshOrderAsync(order.Id, Ct);

        await _audit.Received(2).LogAsync(TicketsAuditActions.TicketCheckoutNeedsAttention, Arg.Any<string>(), order.Id,
            Arg.Any<string>(), Arg.Any<string>());
        _repo.Tickets.Should().OnlyContain(t => t.State == TicketCheckoutTicketState.Valid);
    }

    [HumansFact]
    public async Task Erasure_reaches_the_platform_first_and_a_platform_failure_leaves_everything_for_the_retry()
    {
        await BuyAndPayAsync(Buy(), _buyerId);
        await _service.EraseForUserAsync(_buyerId, Ct);
        await _platform.Received(1).ErasePersonalDataAsync("cs_1", "ana@example.com", Arg.Any<CancellationToken>());

        _platform.ErasePersonalDataAsync(default!, default!).ReturnsForAnyArgs(_ => throw new TicketingPlatformException("down"));
        var act = () => _service.EraseForUserAsync(_buyerId, Ct);
        await act.Should().ThrowAsync<TicketingPlatformException>();
        _repo.Erased.Should().Be(1);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EraseForUserAsync_EmailOnlyHolder_MatchesExport(bool boundElsewhere)
    {
        var order = await BuyAndPayAsync(Buy(), _otherBuyerId);
        var ticket = _repo.Tickets.Single();
        ticket.HolderUserId = boundElsewhere ? _otherBuyerId : null;
        ticket.HolderEmail = "ANA@example.com";
        ticket.HolderName = "Email-only holder";
        order.Lines[0].HolderUserId = ticket.HolderUserId;
        order.Lines[0].HolderEmail = ticket.HolderEmail;
        order.Lines[0].HolderName = ticket.HolderName;
        var json = System.Text.Json.JsonSerializer.Serialize(
            (await _service.ContributeForUserAsync(_buyerId, Ct)).Select(slice => slice.Data));
        if (boundElsewhere)
            json.Should().NotContain("Email-only holder");
        else
            json.Should().Contain("Email-only holder").And.NotContain("Bo Smith").And.NotContain("bo@example.com");

        await _service.EraseForUserAsync(_buyerId, Ct);

        ticket.State.Should().Be(boundElsewhere ? TicketCheckoutTicketState.Valid : TicketCheckoutTicketState.Void);
        (await _service.FindIssuedTicketAsync(ticket.Barcode, Ct))!.IsValid.Should().Be(boundElsewhere);
        if (boundElsewhere)
            await _platform.DidNotReceive().ErasePersonalDataAsync(order.PlatformOrderId!, Arg.Any<string>(), Arg.Any<CancellationToken>());
        else
            await _platform.Received(1).ErasePersonalDataAsync(order.PlatformOrderId!, "ANA@example.com", Ct);
    }

    [HumansFact]
    public async Task EraseForUserAsync_EmailOnlyPlatformFailure_PreservesTicket()
    {
        var order = await BuyAndPayAsync(Buy(), _otherBuyerId);
        var ticket = _repo.Tickets.Single();
        ticket.HolderUserId = null;
        ticket.HolderEmail = "ana@example.com";
        order.Lines[0].HolderUserId = null;
        order.Lines[0].HolderEmail = ticket.HolderEmail;
        _platform.ErasePersonalDataAsync(order.PlatformOrderId!, ticket.HolderEmail, Ct)
            .Returns(_ => throw new TicketingPlatformException("down"));

        var act = () => _service.EraseForUserAsync(_buyerId, Ct);

        await act.Should().ThrowAsync<TicketingPlatformException>();
        ticket.State.Should().Be(TicketCheckoutTicketState.Valid);
        _repo.Erased.Should().Be(0);
    }

    [HumansFact]
    public async Task EraseForUserAsync_SeparateDependentOrder_IncludesOwnedData()
    {
        var adult = await PaidAdultAsync();
        var child = AddTicket(TicketHolderKind.Child, parent: adult.Id);
        child.HolderName = "Separate child";
        child.HolderEmail = "child@example.com";
        var order = new TicketCheckoutOrder
        {
            Id = child.OrderId,
            TicketingSettingsId = _ticketing.Id,
            BuyerUserId = _otherBuyerId,
            BuyerName = "Other buyer",
            PlatformKey = "test",
            PlatformOrderId = "child_order",
            Lines = [new TicketCheckoutOrderLine { Kind = OrderLineKind.Ticket, HolderKind = TicketHolderKind.Child, HolderName = child.HolderName, HolderEmail = child.HolderEmail }],
        };
        _repo.Orders.Add(order);
        var json = System.Text.Json.JsonSerializer.Serialize(
            (await _service.ContributeForUserAsync(_buyerId, Ct)).Select(slice => slice.Data));
        json.Should().Contain(order.Id.ToString()).And.Contain("Separate child").And.NotContain("Other buyer");

        await _service.EraseForUserAsync(_buyerId, Ct);

        child.State.Should().Be(TicketCheckoutTicketState.Void);
        await _platform.Received(1).ErasePersonalDataAsync("child_order", "child@example.com", Ct);
    }

    [HumansFact]
    public async Task A_ticket_in_resale_sells_only_at_the_public_price_so_a_class_price_buyer_takes_fresh_stock_while_there_is_any()
    {
        await OfferedTicketAsync();
        _ticketing.Mode = TicketSalesMode.Open;
        _grants.GetActiveGrantsAsync(_otherBuyerId, Arg.Any<CancellationToken>())
            .Returns([new ActiveGrantInfo(LowIncomeProgram, _event.Id, Now)]);

        var fresh = await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct);

        var line = _repo.Orders.Single(o => o.Id == fresh.OrderId).Lines.Single();
        line.ResaleOfferId.Should().BeNull();
        line.AmountMinor.Should().Be(12500);
        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Open);
    }

    [HumansFact]
    public async Task In_resale_only_mode_every_buyer_pays_the_public_price_less_discounts_and_the_refund_never_exceeds_what_came_in_plus_discounts()
    {
        var old = await OfferedTicketAsync();
        _grants.GetActiveGrantsAsync(_otherBuyerId, Arg.Any<CancellationToken>())
            .Returns([
                new ActiveGrantInfo(LowIncomeProgram, _event.Id, Now),
                new ActiveGrantInfo(DiscountProgram, _event.Id, Now),
            ]);
        (await _service.GetBuyPagesAsync(_otherBuyerId, Ct)).Single().OwnPrice!.AmountMinor.Should().Be(17500);

        var order = await BuyAndPayAsync(Buy(), _otherBuyerId);

        var line = order.Lines.Single();
        line.ResaleOfferId.Should().NotBeNull();
        line.AmountMinor.Should().Be(17500);
        line.ClassLabel.Should().BeNull();
        await RefundBatchAsync();
        var refunded = _repo.Refunds.Single().Refund.AmountMinor;
        refunded.Should().Be(old.PricePaidMinor);
        refunded.Should().BeLessThanOrEqualTo(line.AmountMinor + line.DiscountsMinor);
    }

    [HumansFact]
    public async Task A_caller_that_loses_the_paid_claim_issues_nothing()
    {
        await _service.StartCheckoutAsync(Buy(), _buyerId, Ct);
        _repo.ClaimedPaid.Add(_repo.Orders.Single().Id);

        await PayAsync("cs_1");

        _repo.Tickets.Should().BeEmpty();
        await _delivery.DidNotReceiveWithAnyArgs().TicketsIssuedAsync(default);
    }

    [HumansFact]
    public async Task Reconcile_issues_the_tickets_of_a_paid_order_that_has_none()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        _repo.Tickets.Clear();

        await _service.ReconcileOpenOrdersAsync(Ct);

        _repo.Tickets.Should().ContainSingle(t => t.OrderId == order.Id && t.HolderUserId == _buyerId);
    }

    [HumansFact]
    public async Task PlatformOrderChangedAsync_DonationPaid_QueuesReceiptOnce()
    {
        var order = await BuyAndPayAsync(Buy(forSelf: false, donation: 5000), _buyerId);

        order.State.Should().Be(TicketCheckoutOrderState.Paid);
        _repo.Tickets.Should().BeEmpty();
        await _delivery.Received(1).TicketsIssuedAsync(order.Id, Arg.Any<CancellationToken>());
        await _service.PlatformOrderChangedAsync("test", order.PlatformOrderId!, Ct);
        await _delivery.Received(1).TicketsIssuedAsync(order.Id, Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcileOpenOrdersAsync_DonationReceiptMissing_Requeues(bool initialQueueFails)
    {
        if (initialQueueFails)
            _delivery.TicketsIssuedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(_ => throw new InvalidOperationException("Queue unavailable"));
        var order = await BuyAndPayAsync(Buy(forSelf: false, donation: 5000), _buyerId);
        order.State.Should().Be(TicketCheckoutOrderState.Paid);
        _repo.Tickets.Should().BeEmpty();
        _delivery.TicketsIssuedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _delivery.ClearReceivedCalls();

        await _service.ReconcileOpenOrdersAsync(Ct);
        await _delivery.DidNotReceiveWithAnyArgs().TicketsIssuedAsync(default);
        _clock.Advance(Duration.FromMinutes(31));
        await _service.ReconcileOpenOrdersAsync(Ct);

        await _delivery.Received(1).TicketsIssuedAsync(order.Id, Arg.Any<CancellationToken>());
        await _service.RecordDeliveryAsync(order.Id, TicketCheckoutOrderEventKind.TicketsEmailed, "receipt sent", null, Ct);
        _delivery.ClearReceivedCalls();
        await _service.ReconcileOpenOrdersAsync(Ct);
        await _delivery.DidNotReceiveWithAnyArgs().TicketsIssuedAsync(default);
    }

    [HumansFact]
    public async Task ReconcileOpenOrdersAsync_OldDonation_DoesNotRequeue()
    {
        await BuyAndPayAsync(Buy(forSelf: false, donation: 5000), _buyerId);
        _delivery.ClearReceivedCalls();
        _clock.Advance(Duration.FromDays(8));

        await _service.ReconcileOpenOrdersAsync(Ct);

        await _delivery.DidNotReceiveWithAnyArgs().TicketsIssuedAsync(default);
    }

    [HumansFact]
    public async Task Reconcile_hands_tickets_to_delivery_again_when_no_email_was_recorded()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        _delivery.ClearReceivedCalls();

        await _service.ReconcileOpenOrdersAsync(Ct);
        await _delivery.DidNotReceiveWithAnyArgs().TicketsIssuedAsync(default);

        _clock.Advance(Duration.FromMinutes(31));
        await _service.ReconcileOpenOrdersAsync(Ct);
        await _delivery.Received(1).TicketsIssuedAsync(order.Id, Arg.Any<CancellationToken>());

        await _service.RecordDeliveryAsync(order.Id, TicketCheckoutOrderEventKind.TicketsEmailed, "sent", null, Ct);
        _delivery.ClearReceivedCalls();
        await _service.ReconcileOpenOrdersAsync(Ct);
        await _delivery.DidNotReceiveWithAnyArgs().TicketsIssuedAsync(default);
    }

    [HumansFact]
    public async Task A_resale_refund_that_cannot_start_is_recorded_as_failed_under_the_offer_id()
    {
        var old = await OfferedTicketAsync();
        (await _service.RefundAsync(old.OrderId, 22500, "already refunded", Guid.NewGuid(), Ct)).Succeeded.Should().BeTrue();

        await BuyAndPayAsync(Buy(), _otherBuyerId);
        await RefundBatchAsync();

        var offer = _repo.Offers.Single();
        offer.RefundId.Should().Be(offer.Id);
        var failed = _repo.Refunds.Single(r => r.RefundId == offer.Id).Refund;
        failed.State.Should().Be(RefundState.Failed);
        failed.AmountMinor.Should().Be(22500);
        _repo.Events.Should().Contain(e => e.OrderId == old.OrderId && e.Detail.Contains("refund it by hand", StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task A_resale_refund_whose_platform_call_threw_is_retried_under_the_same_id()
    {
        await OfferedTicketAsync();
        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns<RefundResult>(_ => throw new TicketingPlatformException("timeout"));

        await BuyAndPayAsync(Buy(), _otherBuyerId);
        await RefundBatchAsync();

        var offer = _repo.Offers.Single();
        var refund = _repo.Refunds.Single().Refund;
        refund.Id.Should().Be(offer.Id);
        refund.State.Should().Be(RefundState.Pending);

        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Succeeded, "re_9", null));
        await _service.ReconcileOpenOrdersAsync(Ct);

        _repo.Refunds.Should().ContainSingle();
        refund.State.Should().Be(RefundState.Succeeded);
        await _platform.Received(2).RefundAsync(
            Arg.Is<RefundRequest>(r => r.RefundId == offer.Id && r.PlatformOrderId == "cs_1"), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task A_refund_call_that_throws_stays_pending_so_the_amount_stays_held()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns<RefundResult>(_ => throw new TicketingPlatformException("timeout"));

        (await _service.RefundAsync(order.Id, 10000, "timeout", Guid.NewGuid(), Ct)).Succeeded.Should().BeTrue();

        _repo.Refunds.Single().Refund.State.Should().Be(RefundState.Pending);
        (await _service.GetOrderDetailAsync(order.Id, Ct))!.RefundableMinor.Should().Be(12500);
        var rows = _repo.Events.Count;
        _clock.Advance(Duration.FromSeconds(1));
        await _service.ReconcileOpenOrdersAsync(Ct);
        _repo.Events.Should().HaveCount(rows, "a resend that changes nothing adds no rows");

        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Succeeded, "re_5", null));
        await _service.ReconcileOpenOrdersAsync(Ct);

        _repo.Refunds.Single().Refund.State.Should().Be(RefundState.Succeeded);
        await _platform.Received(3).RefundAsync(
            Arg.Is<RefundRequest>(r => r.RefundId == _repo.Refunds[0].RefundId && r.CreatedAt == Now), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcileOpenOrdersAsync_NewRefundError_PersistsOnce(bool named)
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        var refund = new TicketCheckoutRefund
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            AmountMinor = 5000,
            State = RefundState.Pending,
            PlatformRefundId = named ? "re_ours" : null,
            FailureReason = "timeout",
            CreatedAt = Now,
        };
        _repo.Refunds.Add((refund.Id, refund));
        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns<RefundResult>(_ => throw new TicketingPlatformException("Retry window expired"));

        await _service.ReconcileOpenOrdersAsync(Ct);

        refund.State.Should().Be(RefundState.Pending);
        _repo.RefundWrites.Should().ContainSingle().Which.FailureReason.Should().Be("Retry window expired");
        var events = _repo.Events.Count;
        await _service.ReconcileOpenOrdersAsync(Ct);
        _repo.RefundWrites.Should().ContainSingle();
        _repo.Events.Should().HaveCount(events);
    }

    [HumansTheory]
    [InlineData(RefundState.Pending)]
    [InlineData(RefundState.Succeeded)]
    [InlineData(RefundState.Failed)]
    [InlineData(RefundState.ManualRequired)]
    public async Task PlatformOrderChangedAsync_RefundOutcome_PersistsPayload(RefundState state)
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        var refund = new TicketCheckoutRefund
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            AmountMinor = 5000,
            State = RefundState.Pending,
            PlatformRefundId = "re_ours",
            FailureReason = "old error",
            CreatedAt = Now,
        };
        _repo.Refunds.Add((refund.Id, refund));
        var reason = state is RefundState.Failed or RefundState.ManualRequired ? "Provider instructions" : null;
        var manualUrl = state == RefundState.ManualRequired ? new Uri("https://platform.example/manual") : null;
        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(state, "re_ours", reason, manualUrl));

        await _service.PlatformOrderChangedAsync("test", order.PlatformOrderId!, Ct);

        var saved = _repo.RefundWrites.Should().ContainSingle().Subject;
        saved.State.Should().Be(state);
        saved.FailureReason.Should().Be(reason);
        saved.ManualRefundUrl.Should().Be(manualUrl?.ToString());
    }

    [HumansFact]
    public async Task PlatformOrderChangedAsync_ManualRefundPaid_ClearsInstructions()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        var refund = new TicketCheckoutRefund
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            AmountMinor = 5000,
            State = RefundState.ManualRequired,
            FailureReason = "Pay by hand",
            ManualRefundUrl = "https://platform.example/manual",
            CreatedAt = Now,
        };
        _repo.Refunds.Add((refund.Id, refund));
        _platform.GetStatusAsync(order.PlatformOrderId!, Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 0, "card", Now, RefundedMinor: 5000));

        await _service.PlatformOrderChangedAsync("test", order.PlatformOrderId!, Ct);

        var saved = _repo.RefundWrites.Should().ContainSingle().Subject;
        saved.State.Should().Be(RefundState.Succeeded);
        saved.FailureReason.Should().BeNull();
        saved.ManualRefundUrl.Should().BeNull();
    }

    [HumansTheory]
    [InlineData(998, true)]
    [InlineData(999, false)]
    public async Task RefundAsync_LongFailure_BoundsStoredReason(int prefixLength, bool keepsEmoji)
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        var prefix = new string('x', prefixLength);
        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns<RefundResult>(_ => throw new TicketingPlatformException(prefix + "😀tail"));

        await _service.RefundAsync(order.Id, 5000, "Returned", _buyerId, Ct);

        var saved = _repo.RefundWrites.Should().ContainSingle().Subject;
        saved.FailureReason.Should().Be(prefix + (keepsEmoji ? "😀" : string.Empty));
        saved.State.Should().Be(RefundState.Pending);
        var events = _repo.Events.Count;
        await _service.ReconcileOpenOrdersAsync(Ct);
        _repo.RefundWrites.Should().ContainSingle();
        _repo.Events.Should().HaveCount(events);
    }

    [HumansFact]
    public async Task An_earlier_failed_refund_does_not_fail_a_later_one_still_in_flight()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        _platform.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 0, "card", Now, RefundedMinor: 0, RefundFailed: true));
        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Failed, "re_a", "bank refused"));
        await _service.RefundAsync(order.Id, 10000, "first", Guid.NewGuid(), Ct);
        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Pending, "re_b", null));

        await _service.RefundAsync(order.Id, 5000, "second", Guid.NewGuid(), Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);

        _repo.Refunds.Select(r => r.Refund.State).Should().Equal(RefundState.Failed, RefundState.Pending);

        var second = _repo.Refunds[1].RefundId;
        _platform.RefundAsync(Arg.Is<RefundRequest>(r => r.RefundId == second), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Failed, "re_b", "bank refused"));
        await _service.ReconcileOpenOrdersAsync(Ct);

        _repo.Refunds[1].Refund.State.Should().Be(RefundState.Failed);
    }

    [HumansFact]
    public async Task Several_people_can_be_invited_each_once_and_at_most_ten_per_ticket()
    {
        var old = await OfferedTicketAsync();
        var link = new Uri("https://h.example/r/");

        (await _service.InviteBuyersAsync(old.Id, _buyerId, ["a@example.com", "b@example.com"], link, "en", Ct)).Refusal.Should().BeNull();
        (await _service.InviteBuyersAsync(old.Id, _buyerId, ["A@example.com"], link, "en", Ct)).Refusal.Should().Be("Tickets_Resale_InviteAlready");
        (await _service.InviteBuyersAsync(old.Id, _buyerId, ["b@example.com", "c@example.com"], link, "en", Ct)).Refusal.Should().BeNull();
        (await _service.InviteBuyersAsync(old.Id, _buyerId, ["not an email"], link, "en", Ct)).Refusal.Should().Be("Tickets_Resale_InviteEmailInvalid");
        _repo.Offers.Single().Invites.Select(i => i.Email).Should().Equal("a@example.com", "b@example.com", "c@example.com");
        _repo.Offers.Single().Invites.Select(i => i.TokenHash).Should().OnlyHaveUniqueItems();
        await _email.Received(3).SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());

        (await _service.InviteBuyersAsync(old.Id, _buyerId, [.. Enumerable.Range(0, 8).Select(i => $"d{i}@example.com")], link, "en", Ct))
            .Refusal.Should().Be("Tickets_Resale_InviteLimit");
        (await _service.InviteBuyersAsync(old.Id, _buyerId, [.. Enumerable.Range(0, 7).Select(i => $"d{i}@example.com")], link, "en", Ct))
            .Refusal.Should().BeNull();

        (await _service.WithdrawOfferAsync(old.Id, _buyerId, Ct)).Refusal.Should().BeNull();
        (await _service.OfferForResaleAsync(old.Id, _buyerId, Ct)).Refusal.Should().BeNull();
        (await _service.InviteBuyersAsync(old.Id, _buyerId, ["e@example.com"], link, "en", Ct)).Refusal.Should().Be("Tickets_Resale_InviteLimit");
    }

    [HumansFact]
    public async Task Erasure_voids_the_holders_children_and_passes_every_address_to_the_repository()
    {
        _ticketing.ChildTypes = [new TicketChildType { Name = "Child", UnderAge = 13, PriceMinor = 5000 }];
        await BuyAndPayAsync(Buy() with { Children = [new("Kid García", null)] }, _buyerId);

        await _service.EraseForUserAsync(_buyerId, Ct);

        _repo.Tickets.Should().OnlyContain(t => t.State == TicketCheckoutTicketState.Void);
        _repo.ErasedAddresses.Should().Contain("ana@example.com");
    }

    [HumansFact]
    public async Task The_export_of_someone_bought_for_shows_their_own_line_but_not_who_paid()
    {
        var katie = Human("katie@example.com", "Katie", "Jones");
        await _service.StartCheckoutAsync(Buy(vip: 100000, others: ["katie@example.com"]), _buyerId, Ct);
        await PayAsync("cs_1");

        var json = System.Text.Json.JsonSerializer.Serialize(
            (await _service.ContributeForUserAsync(katie, Ct)).Select(slice => slice.Data));

        json.Should().Contain("Katie Jones").And.NotContain("ana@example.com").And.NotContain("Ana García");
    }

    [HumansFact]
    public async Task At_sell_out_a_class_price_buyer_takes_a_ticket_in_resale_at_the_public_price()
    {
        await OfferedTicketAsync();
        _ticketing.Mode = TicketSalesMode.Open;
        _ticketing.Capacity = 1;
        _grants.GetActiveGrantsAsync(_otherBuyerId, Arg.Any<CancellationToken>())
            .Returns([new ActiveGrantInfo(LowIncomeProgram, _event.Id, Now)]);

        var result = await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct);

        result.Refusal.Should().BeNull();
        var line = _repo.Orders.Single(o => o.Id == result.OrderId).Lines.Single();
        line.ResaleOfferId.Should().Be(_repo.Offers.Single().Id);
        line.AmountMinor.Should().Be(22500);
    }

    [HumansFact]
    public async Task At_sell_out_the_buy_page_shows_the_resale_price_the_order_charges()
    {
        await OfferedTicketAsync();
        _ticketing.Mode = TicketSalesMode.Open;
        _ticketing.Capacity = 1;
        _grants.GetActiveGrantsAsync(_otherBuyerId, Arg.Any<CancellationToken>())
            .Returns([new ActiveGrantInfo(LowIncomeProgram, _event.Id, Now)]);

        (await _service.GetBuyPagesAsync(_otherBuyerId, Ct)).Single().OwnPrice!.AmountMinor.Should().Be(22500);
    }

    public enum OrderRead { Buyer, Holder, Admin }

    private async Task ReadOrderAsync(TicketCheckoutOrder order, OrderRead reader, CancellationToken ct)
    {
        if (reader == OrderRead.Admin)
            await _service.RefreshOrderAsync(order.Id, ct);
        else
            await _service.GetBuyOrderAsync(order.Id, reader == OrderRead.Buyer ? order.BuyerUserId
                : order.Lines.First(l => l.HolderUserId is not null && l.HolderUserId != order.BuyerUserId).HolderUserId!.Value, ct);
    }

    [HumansTheory]
    [InlineData(OrderRead.Buyer)]
    [InlineData(OrderRead.Holder)]
    [InlineData(OrderRead.Admin)]
    public async Task RefreshOrderAsync_LatePaymentMethod_PersistsMetadata(OrderRead reader)
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        order.PaymentMethod = null;
        order.Lines.Add(new TicketCheckoutOrderLine { Kind = OrderLineKind.Ticket, HolderUserId = _otherBuyerId });
        _repo.OrderWrites.Clear();
        _clock.Advance(Duration.FromMinutes(1));
        _platform.GetStatusAsync(order.PlatformOrderId!, Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 0, "ideal", Now));

        await ReadOrderAsync(order, reader, Ct);

        _repo.OrderWrites.Should().Equal((order.Id, "ideal"));
        order.UpdatedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [HumansTheory]
    [InlineData(OrderRead.Buyer, PlatformOrderState.Pending)]
    [InlineData(OrderRead.Holder, PlatformOrderState.Pending)]
    [InlineData(OrderRead.Admin, PlatformOrderState.Pending)]
    [InlineData(OrderRead.Buyer, PlatformOrderState.Expired)]
    [InlineData(OrderRead.Holder, PlatformOrderState.Expired)]
    [InlineData(OrderRead.Admin, PlatformOrderState.Expired)]
    [InlineData(OrderRead.Buyer, PlatformOrderState.Failed)]
    [InlineData(OrderRead.Holder, PlatformOrderState.Failed)]
    [InlineData(OrderRead.Admin, PlatformOrderState.Failed)]
    public async Task RefreshOrderAsync_UnconfirmedClosure_KeepsReservation(OrderRead reader, PlatformOrderState state)
    {
        var offered = await OfferedTicketAsync();
        var started = await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct);
        var order = _repo.Orders.Single(o => o.Id == started.OrderId);
        order.Lines.Add(new TicketCheckoutOrderLine { Kind = OrderLineKind.Ticket, HolderUserId = _buyerId });
        _clock.Advance(Duration.FromHours(2));
        _platform.GetStatusAsync(order.PlatformOrderId!, Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(state, 50, "card", null));

        await ReadOrderAsync(order, reader, Ct);

        await _platform.DidNotReceive().CancelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        order.State.Should().Be(TicketCheckoutOrderState.Pending);
        order.FeeMinor.Should().Be(50);
        _repo.Offers.Single().ReservedByOrderId.Should().Be(order.Id);
        offered.State.Should().Be(TicketCheckoutTicketState.Offered);
    }

    [HumansTheory]
    [InlineData(OrderRead.Buyer)]
    [InlineData(OrderRead.Holder)]
    [InlineData(OrderRead.Admin)]
    public async Task RefreshOrderAsync_PendingRefund_DoesNotReplay(OrderRead reader)
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        order.Lines.Add(new TicketCheckoutOrderLine { Kind = OrderLineKind.Ticket, HolderUserId = _otherBuyerId });
        var refund = new TicketCheckoutRefund
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            AmountMinor = 5000,
            Reason = "Returned",
            State = RefundState.Pending,
            PlatformRefundId = "re_pending",
            CreatedAt = Now,
        };
        _repo.Refunds.Add((refund.Id, refund));
        _platform.GetStatusAsync(order.PlatformOrderId!, Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 30, "card", Now, RefundedMinor: 5000));
        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Succeeded, "re_pending", null));
        _platform.ClearReceivedCalls();

        await ReadOrderAsync(order, reader, Ct);

        await _platform.DidNotReceive().RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
        refund.State.Should().Be(RefundState.Pending);
        order.RefundedMinor.Should().Be(5000);
        await _platform.Received(1).GetStatusAsync(order.PlatformOrderId!, Ct);
    }

    [HumansFact]
    public async Task ReconcileOpenOrdersAsync_CancellationDuringClose_FinishesClosure()
    {
        await _service.StartCheckoutAsync(Buy(), _buyerId, Ct);
        var order = _repo.Orders.Single();
        _clock.Advance(Duration.FromHours(2));
        using var caller = new CancellationTokenSource();
        _platform.GetStatusAsync(order.PlatformOrderId!, caller.Token)
            .Returns(new PlatformOrderStatus(PlatformOrderState.Pending, null, null, null));
        _platform.CancelAsync(order.PlatformOrderId!, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await caller.CancelAsync();
            call.ArgAt<CancellationToken>(1).ThrowIfCancellationRequested();
        });

        await _service.ReconcileOpenOrdersAsync(caller.Token);

        await _platform.Received(1).CancelAsync(order.PlatformOrderId!, CancellationToken.None);
        order.State.Should().Be(TicketCheckoutOrderState.Expired);
        _repo.Events.Should().Contain(e => e.OrderId == order.Id && e.Kind == TicketCheckoutOrderEventKind.StateChanged);
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartCheckoutAsync_PaidBeforeIssuance_KeepsReservations(bool sameHolder)
    {
        _ticketing.Capacity = 1;
        await _service.StartCheckoutAsync(Buy(), _buyerId, Ct);
        var order = _repo.Orders.Single();
        _repo.RejectTicketIssuance = true;
        var pay = () => PayAsync(order.PlatformOrderId!);
        await pay.Should().ThrowAsync<InvalidOperationException>();
        order.State.Should().Be(TicketCheckoutOrderState.Paid);
        _repo.Tickets.Should().BeEmpty();

        var next = await _service.StartCheckoutAsync(Buy(), sameHolder ? _buyerId : _otherBuyerId, Ct);

        next.Refusal.Should().Be(sameHolder ? "Tickets_Buy_AlreadyHolds" : "Tickets_Buy_SoldOut");
        _repo.Orders.Should().ContainSingle();
        _repo.RejectTicketIssuance = false;
        await _service.ReconcileOpenOrdersAsync(Ct);
        _repo.Tickets.Should().ContainSingle(t => t.OrderId == order.Id);
        await _audit.DidNotReceive().LogAsync(TicketsAuditActions.TicketCheckoutNeedsAttention,
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Is<string>(d => d.Contains("already held a ticket", StringComparison.Ordinal)),
            Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<string?>());

        _ticketing.Capacity = 2;
        (await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct)).Refusal.Should().BeNull();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartCheckoutAsync_RemoteHoldPastDeadline_ReservesCapacity(bool recoveryFails)
    {
        _ticketing.Capacity = 1;
        var started = await _service.StartCheckoutAsync(Buy(), _buyerId, Ct);
        var order = _repo.Orders.Single();
        _clock.Advance(Duration.FromHours(2));
        if (recoveryFails)
        {
            _platform.GetStatusAsync(order.PlatformOrderId!, Arg.Any<CancellationToken>())
                .Returns(new PlatformOrderStatus(PlatformOrderState.Expired, null, null, null));
            _platform.CancelAsync(order.PlatformOrderId!, Arg.Any<CancellationToken>())
                .Returns(_ => throw new TicketingPlatformException("Provider unavailable"));
            await _service.ReconcileOpenOrdersAsync(Ct);
        }

        var next = await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct);

        next.Refusal.Should().Be("Tickets_Buy_SoldOut");
        _repo.Orders.Should().ContainSingle();
        var page = (await _service.GetBuyPagesAsync(_buyerId, Ct)).Single();
        page.AlreadyHoldsTicket.Should().BeTrue();
        page.OpenOrder!.Id.Should().Be(started.OrderId!.Value);
        (await _service.StartCheckoutAsync(Buy(), _buyerId, Ct)).Refusal.Should().Be("Tickets_Buy_AlreadyHolds");

        _platform.GetStatusAsync(order.PlatformOrderId!, Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Expired, null, null, null));
        _platform.CancelAsync(order.PlatformOrderId!, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await _service.ReconcileOpenOrdersAsync(Ct);

        order.State.Should().Be(TicketCheckoutOrderState.Expired);
        (await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct)).Refusal.Should().BeNull();
    }

    [HumansFact]
    public async Task StartCheckoutAsync_LocalHoldPastDeadline_ReleasesCapacity()
    {
        _ticketing.Capacity = 1;
        await _service.StartCheckoutAsync(Buy(), _buyerId, Ct);
        var order = _repo.Orders.Single();
        order.PlatformOrderId = null;
        order.State = TicketCheckoutOrderState.Created;
        _clock.Advance(Duration.FromHours(2));

        var next = await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct);

        next.Refusal.Should().BeNull();
        order.HoldsTickets(_clock.GetCurrentInstant()).Should().BeFalse();
    }

    [HumansFact]
    public async Task An_expired_order_stays_open_until_the_platform_cancel_lands()
    {
        await _service.StartCheckoutAsync(Buy(), _buyerId, Ct);
        var order = _repo.Orders.Single();
        _platform.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Expired, null, null, null));
        _platform.CancelAsync("cs_1", Arg.Any<CancellationToken>()).Returns(_ => throw new TicketingPlatformException("down"));

        await _service.ReconcileOpenOrdersAsync(Ct);
        order.State.Should().Be(TicketCheckoutOrderState.Pending);

        _platform.CancelAsync("cs_1", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await _service.ReconcileOpenOrdersAsync(Ct);
        order.State.Should().Be(TicketCheckoutOrderState.Expired);
    }

    [HumansFact]
    public async Task PlatformOrderChangedAsync_DependentWriteFails_WritesNoPartialRetirement()
    {
        _ticketing.ChildTypes = [new TicketChildType { Name = "Child", UnderAge = 13, PriceMinor = 5000 }];
        await OfferedTicketAsync(Buy() with { Children = [new("Kid García", null)] });
        var child = _repo.Tickets.Single(t => t.Kind == TicketHolderKind.Child);
        var started = await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct);
        var order = _repo.Orders.Single(o => o.Id == started.OrderId);
        _platform.GetStatusAsync(order.PlatformOrderId!, Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 0, "card", Now));
        _repo.RejectTicketWrites.Add(child.Id);
        _repo.TicketWrites.Clear();
        var complete = () => _service.PlatformOrderChangedAsync("test", order.PlatformOrderId!, Ct);

        await complete.Should().ThrowAsync<InvalidOperationException>().WithMessage("Ticket write failed");
        _repo.TicketWrites.Should().BeEmpty();
    }

    [HumansFact]
    public async Task A_resale_repeated_after_a_crash_finishes_retiring_the_old_ticket_and_its_children()
    {
        _ticketing.ChildTypes = [new TicketChildType { Name = "Child", UnderAge = 13, PriceMinor = 5000 }];
        var old = await OfferedTicketAsync(Buy() with { Children = [new("Kid García", null)] });
        await BuyAndPayAsync(Buy(), _otherBuyerId);
        var child = _repo.Tickets.Single(t => t.Kind == TicketHolderKind.Child);
        old.State = TicketCheckoutTicketState.Offered;
        child.State = TicketCheckoutTicketState.Valid;
        _repo.Offers.Single().RefundId = null;

        await _service.ReconcileOpenOrdersAsync(Ct);

        old.State.Should().Be(TicketCheckoutTicketState.Resold);
        child.State.Should().Be(TicketCheckoutTicketState.Void);
        _repo.Refunds.Should().BeEmpty("the refund waits for the evening batch");
        await RefundBatchAsync();
        _repo.Refunds.Should().ContainSingle();
    }

    [HumansFact]
    public async Task An_older_pending_refund_that_failed_is_found_even_without_the_failure_flag()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Pending, "re_x", null));
        await _service.RefundAsync(order.Id, 10000, "first", Guid.NewGuid(), Ct);
        await _service.RefundAsync(order.Id, 5000, "second", Guid.NewGuid(), Ct);
        var first = _repo.Refunds[0].RefundId;
        _platform.RefundAsync(Arg.Is<RefundRequest>(r => r.RefundId == first), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Failed, "re_x", "bank refused"));

        await _service.ReconcileOpenOrdersAsync(Ct);

        _repo.Refunds.Select(r => r.Refund.State).Should().Equal(RefundState.Failed, RefundState.Pending);
    }

    [HumansFact]
    public async Task My_tickets_lists_the_valid_children_under_their_adult_and_never_offers_them()
    {
        _ticketing.ChildTypes = [new TicketChildType { Name = "Child", UnderAge = 13, PriceMinor = 5000 }];
        await BuyAndPayAsync(Buy() with { Children = [new("Kid García", null)] }, _buyerId);

        var held = await _service.GetHeldTicketsAsync(_buyerId, Ct);

        held.Select(h => h.Ticket.Kind).Should().Equal(TicketHolderKind.Adult, TicketHolderKind.Child);
        held[1].CanOffer.Should().BeFalse();
        held[0].HasDependents.Should().BeTrue();
    }

    [HumansTheory]
    [InlineData(498, true)]
    [InlineData(499, false)]
    [InlineData(500, false)]
    public async Task Delivery_details_keep_complete_surrogate_pairs_at_the_storage_limit(int prefixLength, bool keepEmoji)
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        var prefix = new string('x', prefixLength);
        var detail = prefix + "😀tail";

        await _service.RecordDeliveryAsync(order.Id, TicketCheckoutOrderEventKind.TicketsEmailed, detail, null, Ct);

        var recorded = _repo.Events.Single(e => e.Kind == TicketCheckoutOrderEventKind.TicketsEmailed).Detail;
        var expected = prefix + (keepEmoji ? "😀" : string.Empty);
        string.Equals(recorded, expected, StringComparison.Ordinal).Should().BeTrue();
    }

    [HumansFact]
    public async Task Delivery_steps_go_on_the_timeline_and_nothing_else_does()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);

        await _service.RecordDeliveryAsync(order.Id, TicketCheckoutOrderEventKind.TicketsEmailed, "sent to ana@example.com", null, Ct);
        var act = () => _service.RecordDeliveryAsync(order.Id, TicketCheckoutOrderEventKind.Voided, "no", null, Ct);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        _repo.Events.Should().ContainSingle(e => e.OrderId == order.Id && e.Kind == TicketCheckoutOrderEventKind.TicketsEmailed);
        order.Reference.Should().HaveLength(8).And.Be(order.Id.ToString("N")[..8].ToUpperInvariant());
    }

    [HumansFact]
    public async Task A_signed_out_visitor_sees_only_selling_events_at_the_public_price()
    {
        _ticketing.PriceRules.Add(new TicketPriceRule { GrantProgramId = Guid.NewGuid(), Kind = TicketPriceRuleKind.Discount, AmountMinor = 9000 });
        var closed = new TicketingSettings
        {
            Id = Guid.NewGuid(),
            EventSettingsId = _event.Id,
            PlatformKey = "test",
            Mode = TicketSalesMode.Closed,
            Waves = [new TicketSalesWave { Name = "Only", EndsOn = new LocalDate(2027, 8, 1), PriceMinor = 1000 }],
        };
        _repo.Settings.Add(closed);

        var sales = await _service.GetPublicSalesAsync(Ct);

        sales.Should().ContainSingle().Which.Should().Be(new PublicSale("Nowhere 2027", 22500, ResaleOnly: false) { GateOpeningDate = _event.GateOpeningDate });
    }

    private TicketCheckoutTicket AddTicket(
        TicketHolderKind kind = TicketHolderKind.Adult, Guid? holder = null, Guid? parent = null,
        TicketCheckoutTicketState state = TicketCheckoutTicketState.Valid, string? classLabel = null)
    {
        var ticket = new TicketCheckoutTicket
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = _ticketing.Id,
            OrderId = Guid.NewGuid(),
            Kind = kind,
            ParentTicketId = parent,
            HolderUserId = holder,
            HolderName = "Ana García",
            Barcode = TicketCheckoutService.NewBarcode(),
            WaveName = "Wave 1",
            ClassLabel = classLabel,
            State = state,
        };
        _repo.Tickets.Add(ticket);
        return ticket;
    }

    [HumansTheory]
    [InlineData((int)TicketHolderKind.Adult, false, false)]
    [InlineData((int)TicketHolderKind.Adult, false, true)]
    [InlineData((int)TicketHolderKind.Child, false, false)]
    [InlineData((int)TicketHolderKind.Child, true, false)]
    [InlineData((int)TicketHolderKind.Companion, false, true)]
    [InlineData((int)TicketHolderKind.Companion, true, true)]
    public async Task FindIssuedTicketAsync_ErasedIdentity_RefusesAdmission(int holderKind, bool erasedParent, bool emailOnly)
    {
        var kind = (TicketHolderKind)holderKind;
        var adult = AddTicket(holder: _buyerId);
        var ticket = kind == TicketHolderKind.Adult ? adult : AddTicket(kind, parent: adult.Id);
        var erased = erasedParent ? adult : ticket;
        erased.HolderName = emailOnly ? "Original name" : TicketPiiTombstone.Name;
        erased.HolderEmail = emailOnly ? TicketPiiTombstone.EmailFor(_buyerId) : "old@example.com";

        var scanned = (await _service.FindIssuedTicketAsync(ticket.Barcode, Ct))!;
        var listed = (await _service.GetIssuedTicketsForEventAsync(_event.Id, Ct)).Single(t => t.Id == ticket.Id);

        scanned.IsValid.Should().BeFalse();
        scanned.InResaleQueue.Should().BeFalse();
        listed.IsValid.Should().BeFalse();
        listed.InResaleQueue.Should().BeFalse();
        ticket.State.Should().Be(TicketCheckoutTicketState.Valid);
        adult.State.Should().Be(TicketCheckoutTicketState.Valid);
        _repo.TicketWrites.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Gate_finds_an_issued_ticket_by_its_code_with_its_event_and_holder()
    {
        var ticket = AddTicket(holder: _buyerId, classLabel: "Low income");

        var found = await _service.FindIssuedTicketAsync($" {ticket.Barcode} ", TestContext.Current.CancellationToken);

        found.Should().Be(new IssuedTicketInfo(ticket.Id, _event.Id, ticket.Barcode, "Ana García", _buyerId, _buyerId,
            "Low income", IsValid: true));
        (await _service.FindIssuedTicketAsync("HNOSUCHCODE", TestContext.Current.CancellationToken)).Should().BeNull();
        (await _service.FindIssuedTicketAsync(" ", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [HumansTheory]
    [InlineData((int)TicketCheckoutTicketState.Valid, false, true, false)]
    [InlineData((int)TicketCheckoutTicketState.Offered, false, false, true)]
    [InlineData((int)TicketCheckoutTicketState.Offered, true, true, false)]
    [InlineData((int)TicketCheckoutTicketState.Resold, false, false, false)]
    [InlineData((int)TicketCheckoutTicketState.Void, false, false, false)]
    public async Task Gate_refuses_a_ticket_that_can_still_sell_on_resale_and_resold_or_void_ones(
        int state, bool salesClosed, bool valid, bool inQueue)
    {
        var ticket = AddTicket(holder: _buyerId, state: (TicketCheckoutTicketState)state);
        if (salesClosed)
            _ticketing.Mode = TicketSalesMode.Closed;

        var found = await _service.FindIssuedTicketAsync(ticket.Barcode, TestContext.Current.CancellationToken);

        found!.IsValid.Should().Be(valid);
        found.InResaleQueue.Should().Be(inQueue);
    }

    [HumansFact]
    public async Task Gate_refuses_a_child_ticket_while_its_adults_offer_can_still_sell()
    {
        var adult = AddTicket(holder: _buyerId, state: TicketCheckoutTicketState.Offered);
        var child = AddTicket(TicketHolderKind.Child, parent: adult.Id);

        var found = await _service.FindIssuedTicketAsync(child.Barcode, TestContext.Current.CancellationToken);
        found!.IsValid.Should().BeFalse();
        found.InResaleQueue.Should().BeTrue();

        _ticketing.Mode = TicketSalesMode.Closed;
        (await _service.FindIssuedTicketAsync(child.Barcode, TestContext.Current.CancellationToken))!.IsValid.Should().BeTrue();
    }

    [HumansFact]
    public async Task An_offer_whose_child_was_used_at_the_gate_never_sells()
    {
        _ticketing.ChildTypes = [new TicketChildType { Name = "Child", UnderAge = 13, PriceMinor = 5000 }];
        var old = await OfferedTicketAsync(Buy() with { Children = [new("Kid García", null)] });
        _admitted.Add(_repo.Tickets.Single(t => t.OrderId == old.OrderId && t.Kind == TicketHolderKind.Child).Barcode);

        (await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct)).Refusal.Should().Be("Tickets_Buy_ResaleOnly");
    }

    [HumansTheory]
    [InlineData((int)TicketHolderKind.Child, (int)TicketCheckoutTicketState.Resold)]
    [InlineData((int)TicketHolderKind.Child, (int)TicketCheckoutTicketState.Void)]
    [InlineData((int)TicketHolderKind.Companion, (int)TicketCheckoutTicketState.Resold)]
    [InlineData((int)TicketHolderKind.Companion, (int)TicketCheckoutTicketState.Void)]
    [InlineData((int)TicketHolderKind.Child, -1)]
    [InlineData((int)TicketHolderKind.Companion, -1)]
    public async Task FindIssuedTicketAsync_RetiredParent_RefusesDependent(int kind, int state)
    {
        var parentId = state < 0 ? Guid.NewGuid() : AddTicket(holder: _buyerId, state: (TicketCheckoutTicketState)state).Id;
        var dependent = AddTicket((TicketHolderKind)kind, parent: parentId);

        (await _service.FindIssuedTicketAsync(dependent.Barcode, Ct))!.IsValid.Should().BeFalse();
        (await _service.GetIssuedTicketsForEventAsync(_event.Id, Ct)).Single(t => t.Id == dependent.Id).IsValid.Should().BeFalse();
    }

    [HumansFact]
    public async Task Gate_takes_a_childs_early_entry_from_the_adult_it_belongs_to()
    {
        var adult = AddTicket(holder: _buyerId);
        var child = AddTicket(TicketHolderKind.Child, parent: adult.Id);

        var found = await _service.FindIssuedTicketAsync(child.Barcode, TestContext.Current.CancellationToken);

        found!.HolderUserId.Should().BeNull();
        found.EntryUserId.Should().Be(_buyerId);
        found.TicketLabel.Should().Be("Child · Wave 1");
    }

    [HumansFact]
    public async Task Gate_lists_every_issued_ticket_of_an_event_for_name_search()
    {
        var adult = AddTicket(holder: _buyerId);
        var child = AddTicket(TicketHolderKind.Child, parent: adult.Id);
        var gone = AddTicket(holder: _buyerId, state: TicketCheckoutTicketState.Void);

        var all = await _service.GetIssuedTicketsForEventAsync(_event.Id, Ct);

        all.Select(t => t.Id).Should().BeEquivalentTo([adult.Id, child.Id, gone.Id]);
        all.Single(t => t.Id == child.Id).EntryUserId.Should().Be(_buyerId);
        all.Single(t => t.Id == gone.Id).IsValid.Should().BeFalse();
        (await _service.GetIssuedTicketsForEventAsync(Guid.NewGuid(), Ct)).Should().BeEmpty();
    }

    private sealed class FakeCheckoutRepository : ITicketCheckoutRepository
    {
        public List<TicketingSettings> Settings { get; } = [];
        public List<TicketCheckoutOrder> Orders { get; } = [];
        public List<(Guid RefundId, TicketCheckoutRefund Refund)> Refunds { get; } = [];

        public Task<IReadOnlyList<TicketingSettings>> ListTicketingSettingsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketingSettings>>(Settings.ToList());
        public Task<TicketingSettings?> GetTicketingSettingsAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Settings.FirstOrDefault(e => e.Id == id));
        public Task<TicketingSettings?> GetTicketingSettingsForEventAsync(Guid eventSettingsId, CancellationToken ct = default) =>
            Task.FromResult(Settings.FirstOrDefault(e => e.EventSettingsId == eventSettingsId));
        public Task SaveTicketingSettingsAsync(TicketingSettings ticketing, CancellationToken ct = default)
        {
            Settings.RemoveAll(e => e.Id == ticketing.Id);
            Settings.Add(ticketing);
            return Task.CompletedTask;
        }
        public Task<TicketCheckoutOrder?> GetOrderAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Orders.FirstOrDefault(o => o.Id == id));
        public Task<TicketCheckoutOrder?> GetOrderByPlatformIdAsync(string platformKey, string platformOrderId, CancellationToken ct = default) =>
            Task.FromResult(Orders.FirstOrDefault(o => string.Equals(o.PlatformKey, platformKey, StringComparison.Ordinal) && string.Equals(o.PlatformOrderId, platformOrderId, StringComparison.Ordinal)));
        public Task<IReadOnlyList<TicketCheckoutOrder>> GetOrdersForSettingsAsync(Guid settingsId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketCheckoutOrder>>(Orders.Where(o => o.TicketingSettingsId == settingsId).ToList());
        public Task<IReadOnlyList<TicketCheckoutOrder>> GetOrdersForUserAsync(Guid userId, CancellationToken ct = default, IReadOnlyCollection<string>? emails = null) =>
            Task.FromResult<IReadOnlyList<TicketCheckoutOrder>>(
                Orders.Where(o => o.BuyerUserId == userId || o.Lines.Any(l => l.HolderUserId == userId
                    || (l.HolderUserId is null && l.HolderEmail is { } email && emails?.Contains(email, StringComparer.OrdinalIgnoreCase) == true))).ToList());
        public Task<IReadOnlyList<TicketCheckoutOrder>> GetOpenOrdersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketCheckoutOrder>>(Orders
                .Where(o => o.IsOpen).ToList());
        public bool RejectCompanionWrites { get; set; }

        public Task AddOrderAsync(TicketCheckoutOrder order, CancellationToken ct = default, TicketCheckoutTicket? ticket = null)
        {
            if (RejectCompanionWrites && ticket is not null)
                throw new InvalidOperationException("Companion write failed");
            Orders.Add(order);
            if (ticket is not null)
                Tickets.Add(ticket);
            return Task.CompletedTask;
        }
        public List<(Guid OrderId, string? PaymentMethod)> OrderWrites { get; } = [];

        public bool RejectOrderWrites { get; set; }
        public int FailOrderWrites { get; set; }

        public Task UpdateOrderAsync(TicketCheckoutOrder order, CancellationToken ct = default, TicketCheckoutTicket? ticket = null)
        {
            if (RejectOrderWrites)
                throw new InvalidOperationException("Order write failed");
            if (FailOrderWrites > 0)
            {
                FailOrderWrites--;
                throw new InvalidOperationException("Order write failed");
            }
            if (ticket is not null && RejectTicketWrites.Contains(ticket.Id))
                throw new InvalidOperationException("Ticket write failed");
            OrderWrites.Add((order.Id, order.PaymentMethod));
            if (ticket is not null)
                TicketWrites.Add((ticket.Id, ticket.State));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<TicketCheckoutRefund>> GetRefundsAsync(Guid orderId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketCheckoutRefund>>(Refunds.Select(r => r.Refund).Where(r => r.OrderId == orderId).ToList());
        public Task AddRefundAsync(TicketCheckoutRefund refund, CancellationToken ct = default)
        {
            Refunds.Add((refund.Id, refund));
            return Task.CompletedTask;
        }
        public List<(Guid RefundId, RefundState State, string? FailureReason, string? ManualRefundUrl)> RefundWrites { get; } = [];

        public Task UpdateRefundAsync(TicketCheckoutRefund refund, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RefundWrites.Add((refund.Id, refund.State, refund.FailureReason, refund.ManualRefundUrl));
            return Task.CompletedTask;
        }

        public List<TicketCheckoutOrderEvent> Events { get; } = [];

        public TicketCheckoutOrderEventKind? RejectEventKind { get; set; }

        public Task AddOrderEventAsync(TicketCheckoutOrderEvent orderEvent, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (orderEvent.Kind == RejectEventKind)
                throw new InvalidOperationException("Timeline write failed");
            Events.Add(orderEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TicketCheckoutOrderEvent>> GetOrderEventsAsync(Guid orderId, CancellationToken ct = default) =>
            GetOrderEventsAsync([orderId], ct);
        public Task<IReadOnlyList<TicketCheckoutOrderEvent>> GetOrderEventsAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketCheckoutOrderEvent>>(Events.Where(e => orderIds.Contains(e.OrderId)).ToList());
        public Task MarkCancellationEmailedAsync(Guid orderId, Instant at, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Orders.Single(o => o.Id == orderId).CancellationEmailedAt ??= at;
            return Task.CompletedTask;
        }
        public List<TicketCheckoutTicket> Tickets { get; } = [];
        public List<TicketResaleOffer> Offers { get; } = [];

        public HashSet<Guid> ClaimedPaid { get; } = [];

        public Task<bool> TryClaimPaidAsync(Guid orderId, Instant paidAt, CancellationToken ct = default) =>
            Task.FromResult(ClaimedPaid.Add(orderId));

        public bool RejectTicketIssuance { get; set; }

        public Task<bool> AddTicketsAsync(IReadOnlyList<TicketCheckoutTicket> tickets, CancellationToken ct = default)
        {
            if (RejectTicketIssuance)
                throw new InvalidOperationException("Ticket issuance failed");
            if (RejectCompanionWrites && tickets.Any(t => t.Kind == TicketHolderKind.Companion))
                throw new InvalidOperationException("Companion write failed");
            if (tickets.Any(n => Tickets.Any(t => t.OrderId == n.OrderId && t.LineIndex == n.LineIndex)))
                return Task.FromResult(false);
            Tickets.AddRange(tickets);
            return Task.FromResult(true);
        }
        public HashSet<Guid> RejectTicketWrites { get; } = [];
        public List<(Guid Id, TicketCheckoutTicketState State)> TicketWrites { get; } = [];

        public Task UpdateTicketAsync(TicketCheckoutTicket ticket, CancellationToken ct = default,
            IReadOnlyList<TicketCheckoutTicket>? dependents = null, TicketResaleOffer? offer = null)
        {
            var changed = new[] { ticket }.Concat(dependents ?? []).ToList();
            if (changed.Any(t => RejectTicketWrites.Contains(t.Id)))
                throw new InvalidOperationException("Ticket write failed");
            TicketWrites.AddRange(changed.Select(t => (t.Id, t.State)));
            if (offer is not null)
                OfferWrites.Add((offer.Id, offer.State));
            return Task.CompletedTask;
        }
        public Task<TicketCheckoutTicket?> GetTicketAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Tickets.FirstOrDefault(t => t.Id == id));
        public Task<TicketCheckoutTicket?> GetTicketByBarcodeAsync(string barcode, CancellationToken ct = default) =>
            Task.FromResult(Tickets.FirstOrDefault(t => string.Equals(t.Barcode, barcode, StringComparison.Ordinal)));
        public Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForOrderAsync(Guid orderId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketCheckoutTicket>>(Tickets.Where(t => t.OrderId == orderId).ToList());
        public Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForHolderAsync(Guid userId, CancellationToken ct = default, IReadOnlyCollection<string>? emails = null) =>
            Task.FromResult<IReadOnlyList<TicketCheckoutTicket>>(Tickets.Where(t => t.HolderUserId == userId
                || (t.HolderUserId is null && t.HolderEmail is { } email && emails?.Contains(email, StringComparer.OrdinalIgnoreCase) == true)).ToList());
        public Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForSettingsAsync(Guid settingsId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketCheckoutTicket>>(Tickets.Where(t => t.TicketingSettingsId == settingsId).ToList());
        public Task AddOfferAsync(TicketResaleOffer offer, CancellationToken ct = default, TicketCheckoutTicket? ticket = null)
        {
            if (ticket is not null && RejectTicketWrites.Contains(ticket.Id))
                throw new InvalidOperationException("Ticket write failed");
            Offers.Add(offer);
            if (ticket is not null)
                TicketWrites.Add((ticket.Id, ticket.State));
            return Task.CompletedTask;
        }
        public List<(Guid Id, TicketResaleOfferState State)> OfferWrites { get; } = [];

        public Task UpdateOfferAsync(TicketResaleOffer offer, CancellationToken ct = default)
        {
            OfferWrites.Add((offer.Id, offer.State));
            return Task.CompletedTask;
        }
        public Task<TicketResaleOffer?> GetOfferAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Offers.FirstOrDefault(o => o.Id == id));
        public Task<TicketResaleOffer?> GetOfferByTokenHashAsync(string tokenHash, CancellationToken ct = default) =>
            Task.FromResult(Offers.FirstOrDefault(o => o.InviteFor(tokenHash) is not null));
        public Task<IReadOnlyList<TicketResaleOffer>> GetOffersForSettingsAsync(Guid settingsId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketResaleOffer>>(Offers.Where(o => o.TicketingSettingsId == settingsId).ToList());
        public List<TicketCompanionRequest> CompanionRequests { get; } = [];
        public Task AddCompanionRequestAsync(TicketCompanionRequest request, CancellationToken ct = default)
        {
            CompanionRequests.Add(request);
            return Task.CompletedTask;
        }
        public Task UpdateCompanionRequestAsync(TicketCompanionRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task<TicketCompanionRequest?> GetCompanionRequestAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(CompanionRequests.FirstOrDefault(r => r.Id == id));
        public Task<IReadOnlyList<TicketCompanionRequest>> GetCompanionRequestsForSettingsAsync(Guid settingsId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketCompanionRequest>>(CompanionRequests.Where(r => r.TicketingSettingsId == settingsId).ToList());
        public Task<IReadOnlyList<TicketCompanionRequest>> GetCompanionRequestsForUserAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketCompanionRequest>>(
                CompanionRequests.Where(r => r.HolderUserId == userId || r.CompanionUserId == userId).ToList());
        public Task<IReadOnlyList<TicketCheckoutRefund>> GetRefundsForSettingsAsync(Guid settingsId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketCheckoutRefund>>(Refunds.Select(r => r.Refund)
                .Where(r => Orders.Any(o => o.Id == r.OrderId && o.TicketingSettingsId == settingsId)).ToList());
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

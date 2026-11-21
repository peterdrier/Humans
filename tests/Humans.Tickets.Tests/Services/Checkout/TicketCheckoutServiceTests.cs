using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Configuration;
using Humans.Consent.Contracts;
using Humans.Email.Contracts;
using Humans.Grants.Contracts;
using Humans.Settings.Contracts;
using Humans.Testing;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Services;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    private readonly IUserEmailService _userEmails = Substitute.For<IUserEmailService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly IConsentService _consents = Substitute.For<IConsentService>();
    private readonly IConsentSubmission _consentSubmission = Substitute.For<IConsentSubmission>();
    private readonly ICommunicationPreferenceService _preferenceReads = Substitute.For<ICommunicationPreferenceService>();
    private readonly ICommunicationPreferenceWrite _preferences = Substitute.For<ICommunicationPreferenceWrite>();
    private readonly IEmailService _email = Substitute.For<IEmailService>();
    private readonly ITicketDelivery _delivery = Substitute.For<ITicketDelivery>();
    private readonly FakeClock _clock = new(Now);
    private readonly Guid _otherBuyerId = Guid.NewGuid();
    private int _sessions;
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

        _service = new TicketCheckoutService(_repo, [_backend], _grants, _settings, _users, _provisioning, _userEmails,
            _consents, _consentSubmission, _preferenceReads, _preferences, _email, emails, [_delivery], localizer, _audit,
            _clock, NullLogger<TicketCheckoutService>.Instance);
    }

    private StartCheckout Buy(
        bool forSelf = true, string? partnerName = null, string? partnerEmail = null, long? supporter = null, long donation = 0,
        Guid? salesEventId = null) =>
        new(salesEventId ?? _salesEvent.Id, forSelf, partnerName, partnerEmail, supporter, donation, TicketDonationPurpose.General,
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
    public async Task Partner_pays_the_public_price_and_gets_an_account_only_once_the_order_is_paid()
    {
        _grants.GetActiveGrantsAsync(_buyerId, Arg.Any<CancellationToken>())
            .Returns([new ActiveGrantInfo(Guid.NewGuid(), LowIncomeProgram, "Low income", _event.Id, Now)]);
        var partnerId = Guid.NewGuid();
        _provisioning.FindOrCreateUserByEmailAsync("bo@example.com", null, ContactSource.Manual, Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(partnerId, Created: true));

        var result = await _service.StartCheckoutAsync(
            Buy(partnerName: "Bo Smith", partnerEmail: "bo@example.com"), _buyerId, TestContext.Current.CancellationToken);

        result.Refusal.Should().BeNull();
        var lines = _repo.Orders.Single().Lines;
        lines.Should().HaveCount(2);
        lines[0].AmountMinor.Should().Be(12500);
        lines[1].AmountMinor.Should().Be(22500);
        lines[1].AttendeeUserId.Should().BeNull();
        await _provisioning.DidNotReceiveWithAnyArgs().FindOrCreateUserByEmailAsync(default!, default, default, default);

        await PayAsync("cs_1");

        lines[1].AttendeeUserId.Should().Be(partnerId);
        _repo.Tickets.Should().Contain(t => t.HolderUserId == partnerId && t.HolderName == "Bo Smith");
    }

    [HumansFact]
    public async Task A_verified_partner_address_attaches_the_ticket_to_that_account()
    {
        var partnerId = Guid.NewGuid();
        _userEmails.FindByAddressAsync("bo@example.com", true, true, Arg.Any<CancellationToken>())
            .Returns([Row(partnerId, verified: true)]);

        await _service.StartCheckoutAsync(Buy(partnerName: "Bo Smith", partnerEmail: "bo@example.com"), _buyerId, Ct);
        await PayAsync("cs_1");

        _repo.Tickets.Should().Contain(t => t.HolderUserId == partnerId);
        await _provisioning.DidNotReceiveWithAnyArgs().FindOrCreateUserByEmailAsync(default!, default, default, default);
    }

    [HumansFact]
    public async Task A_single_unverified_partner_address_is_replaced_by_a_new_account()
    {
        var owner = Guid.NewGuid();
        var row = Row(owner, verified: false);
        var partnerId = Guid.NewGuid();
        _userEmails.FindByAddressAsync("bo@example.com", true, false, Arg.Any<CancellationToken>()).Returns([row]);
        _provisioning.ReplaceUnverifiedEmailAndProvisionAsync(owner, row.Id, "bo@example.com", null, ContactSource.Manual,
                Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(partnerId, Created: true));

        await _service.StartCheckoutAsync(Buy(partnerName: "Bo Smith", partnerEmail: "bo@example.com"), _buyerId, Ct);
        await PayAsync("cs_1");

        _repo.Tickets.Should().Contain(t => t.HolderUserId == partnerId);
    }

    [HumansFact]
    public async Task An_ambiguous_partner_address_issues_the_ticket_without_an_account_and_says_so()
    {
        _userEmails.FindByAddressAsync("bo@example.com", true, true, Arg.Any<CancellationToken>())
            .Returns([Row(Guid.NewGuid(), verified: true), Row(Guid.NewGuid(), verified: true)]);

        await _service.StartCheckoutAsync(Buy(partnerName: "Bo Smith", partnerEmail: "bo@example.com"), _buyerId, Ct);
        await PayAsync("cs_1");

        _repo.Tickets.Should().HaveCount(2).And.Contain(t => t.HolderUserId == null && t.HolderName == "Bo Smith");
        _repo.Events.Should().Contain(e => e.Detail.Contains("verified on more than one account", StringComparison.Ordinal));
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCheckoutNeedsAttention, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Is<string>(d => d.Contains("verified on more than one account")), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<string?>());
        await _provisioning.DidNotReceiveWithAnyArgs().FindOrCreateUserByEmailAsync(default!, default, default, default);
    }

    private static UserEmailRowSnapshot Row(Guid userId, bool verified) =>
        new(Guid.NewGuid(), userId, "bo@example.com", verified, null, null, IsPrimary: true, null, null, Now, Now);

    private async Task PayAsync(string session, Instant? paidAt = null)
    {
        _backend.GetStatusAsync(session, Arg.Any<CancellationToken>())
            .Returns(new CheckoutStatus(CheckoutState.Paid, 100, "card", paidAt ?? Now));
        await _service.CheckoutChangedAsync("test", session, Ct);
    }

    [HumansFact]
    public async Task A_refused_supporter_total_creates_no_partner_account()
    {
        var result = await _service.StartCheckoutAsync(
            Buy(partnerName: "Bo Smith", partnerEmail: "bo@example.com", supporter: 12345), _buyerId, TestContext.Current.CancellationToken);

        result.Refusal.Should().Be("Tickets_Buy_Invalid");
        await _provisioning.DidNotReceiveWithAnyArgs().FindOrCreateUserByEmailAsync(default!, default, default, default);
        _repo.Orders.Should().BeEmpty();
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
    public async Task Webhook_nudge_reads_the_backend_status_and_issues_each_ticket_once()
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
    public async Task Two_events_sell_at_once_each_with_its_own_holding_check_and_no_active_event_needed()
    {
        _settings.GetActiveEventSettings().Returns((EventSettingsInfo?)null);
        var other = new EventSettingsInfo(Guid.NewGuid(), "Elsewhere 2027", 2027, "Europe/Madrid", new LocalDate(2027, 9, 1),
            -7, 5, 8, new Dictionary<int, int>(), null, null);
        _settings.GetEventSettingsByIdAsync(other.Id, Arg.Any<CancellationToken>()).Returns(other);
        var otherSales = new TicketSalesEvent
        {
            Id = Guid.NewGuid(),
            EventSettingsId = other.Id,
            BackendKey = "test",
            Mode = TicketSalesMode.Open,
            Capacity = 10,
            Waves = [new TicketSalesWave { Name = "Only", EndsOn = new LocalDate(2027, 8, 1), PriceMinor = 9000 }],
        };
        _repo.SalesEvents.Add(otherSales);

        (await _service.StartCheckoutAsync(Buy(), _buyerId, TestContext.Current.CancellationToken)).Refusal.Should().BeNull();
        var pages = await _service.GetBuyPagesAsync(_buyerId, TestContext.Current.CancellationToken);

        pages.Select(p => p.EventName).Should().Equal("Nowhere 2027", "Elsewhere 2027");
        pages[0].AlreadyHoldsTicket.Should().BeTrue();
        pages[1].AlreadyHoldsTicket.Should().BeFalse();
        (await _service.StartCheckoutAsync(Buy(salesEventId: otherSales.Id), _buyerId, TestContext.Current.CancellationToken))
            .Refusal.Should().BeNull();
        _repo.Orders.Last().TotalMinor.Should().Be(9000);
        await _backend.Received(1).CreateCheckoutAsync(
            Arg.Is<CheckoutRequest>(r => r.EventId == other.Id), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Timeline_records_each_step_with_who_did_it()
    {
        _backend.CreateCheckoutAsync(Arg.Any<CheckoutRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CheckoutSession("cs_1", new Uri("https://pay.example/cs_1"), new Uri("https://admin.example/cs_1")));
        await _service.StartCheckoutAsync(Buy(), _buyerId, TestContext.Current.CancellationToken);
        _backend.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new CheckoutStatus(CheckoutState.Paid, 455, "card", Now));
        await _service.CheckoutChangedAsync("test", "cs_1", TestContext.Current.CancellationToken);
        var order = _repo.Orders.Single();
        _backend.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Succeeded, "re_1", null));
        _backend.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new CheckoutStatus(CheckoutState.Paid, 455, "card", Now, RefundedMinor: 5000));
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
    public async Task Refund_of_an_unpaid_order_is_refused_without_calling_the_backend()
    {
        await _service.StartCheckoutAsync(Buy(), _buyerId, TestContext.Current.CancellationToken);

        (await _service.RefundAsync(_repo.Orders.Single().Id, 100, "nope", Guid.NewGuid(), TestContext.Current.CancellationToken))
            .Succeeded.Should().BeFalse();
        await _backend.DidNotReceive().RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<TicketCheckoutOrder> BuyAndPayAsync(StartCheckout command, Guid userId)
    {
        var result = await _service.StartCheckoutAsync(command, userId, Ct);
        result.Refusal.Should().BeNull();
        var order = _repo.Orders.Single(o => o.Id == result.OrderId);
        _backend.GetStatusAsync(order.BackendOrderId!, Arg.Any<CancellationToken>())
            .Returns(new CheckoutStatus(CheckoutState.Paid, 0, "card", _clock.GetCurrentInstant()));
        await _service.CheckoutChangedAsync("test", order.BackendOrderId!, Ct);
        return order;
    }

    private async Task<TicketCheckoutTicket> OfferedTicketAsync(StartCheckout? command = null)
    {
        var order = await BuyAndPayAsync(command ?? Buy(), _buyerId);
        var adult = _repo.Tickets.Single(t => t.OrderId == order.Id && t.Kind == TicketHolderKind.Adult);
        (await _service.OfferForResaleAsync(adult.Id, _buyerId, Ct)).Refusal.Should().BeNull();
        _salesEvent.Mode = TicketSalesMode.ResaleOnly;
        return adult;
    }

    [HumansFact]
    public async Task Children_are_issued_with_the_adult_and_need_a_child_price()
    {
        (await _service.StartCheckoutAsync(Buy() with { ChildNames = ["Kid García"] }, _buyerId, Ct))
            .Refusal.Should().Be("Tickets_Buy_Invalid");

        _salesEvent.ChildPriceMinor = 5000;
        var order = await BuyAndPayAsync(Buy() with { ChildNames = ["Kid García"] }, _buyerId);

        order.TotalMinor.Should().Be(27500);
        var adult = _repo.Tickets.Single(t => t.Kind == TicketHolderKind.Adult);
        var child = _repo.Tickets.Single(t => t.Kind == TicketHolderKind.Child);
        child.ParentTicketId.Should().Be(adult.Id);
        child.HolderName.Should().Be("Kid García");
        child.PricePaidMinor.Should().Be(5000);
    }

    [HumansFact]
    public async Task Ticket_rules_must_be_accepted_and_are_signed_through_consent()
    {
        var teamId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        _salesEvent.TermsTeamId = teamId;
        _consents.GetRequiredConsentRowsForUserAsync(_buyerId, teamId, Arg.Any<CancellationToken>())
            .Returns([new RequiredConsentRow(versionId, "Ticket rules", Signed: false)]);
        _consentSubmission.SubmitConsentAsync(_buyerId, versionId, true, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConsentSubmitResult(true, "Ticket rules"));

        (await _service.StartCheckoutAsync(Buy(), _buyerId, Ct)).Refusal.Should().Be("Tickets_Buy_TermsRequired");
        _repo.Orders.Should().BeEmpty();

        var result = await _service.StartCheckoutAsync(Buy() with { AcceptTerms = true, IpAddress = "203.0.113.9" }, _buyerId, Ct);

        result.Refusal.Should().BeNull();
        await _consentSubmission.Received(1).SubmitConsentAsync(
            _buyerId, versionId, true, "203.0.113.9", Arg.Any<string>(), Arg.Any<CancellationToken>());
        _repo.Events.Should().Contain(e => e.Kind == TicketCheckoutOrderEventKind.TermsAccepted && e.Detail.Contains("Ticket rules"));
    }

    [HumansFact]
    public async Task A_failed_consent_refuses_the_checkout()
    {
        var teamId = Guid.NewGuid();
        _salesEvent.TermsTeamId = teamId;
        _consents.GetRequiredConsentRowsForUserAsync(_buyerId, teamId, Arg.Any<CancellationToken>())
            .Returns([new RequiredConsentRow(Guid.NewGuid(), "Ticket rules", Signed: false)]);
        _consentSubmission.SubmitConsentAsync(default, default, default, default!, default!, default)
            .ReturnsForAnyArgs(new ConsentSubmitResult(false, ErrorKey: "ContentUnavailable"));

        (await _service.StartCheckoutAsync(Buy() with { AcceptTerms = true }, _buyerId, Ct))
            .Refusal.Should().Be("Tickets_Buy_TermsFailed");
        _repo.Orders.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Mailing_list_opt_in_is_saved_only_when_the_buyer_has_no_choice_on_record()
    {
        await _service.StartCheckoutAsync(Buy() with { MarketingOptIn = true }, _buyerId, Ct);

        await _preferences.Received(1).UpdatePreferenceAsync(_buyerId, MessageCategory.Marketing, false, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Resale_sells_the_oldest_offer_first_voids_children_and_refunds_the_holder_what_they_paid()
    {
        _salesEvent.ChildPriceMinor = 5000;
        var old = await OfferedTicketAsync(Buy() with { ChildNames = ["Kid García"] });
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
        await _backend.Received(1).RefundAsync(
            Arg.Is<RefundRequest>(r => r.BackendOrderId == "cs_1" && r.AmountMinor == 27500), Arg.Any<CancellationToken>());
        offer.RefundId.Should().NotBeNull();
        _repo.Events.Should().Contain(e => e.OrderId == old.OrderId && e.Kind == TicketCheckoutOrderEventKind.ResaleSold);
    }

    [HumansFact]
    public async Task An_offer_held_by_a_checkout_cannot_be_withdrawn_and_returns_to_the_queue_when_it_is_cancelled()
    {
        var old = await OfferedTicketAsync();
        var pending = await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct);
        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Reserved);

        (await _service.WithdrawOfferAsync(old.Id, _buyerId, Ct)).Refusal.Should().Be("Tickets_Resale_InCheckout");
        _backend.GetStatusAsync("cs_2", Arg.Any<CancellationToken>())
            .Returns(new CheckoutStatus(CheckoutState.Pending, null, null, null));
        (await _service.CancelAsync(pending.OrderId!.Value, _otherBuyerId, Ct)).Refusal.Should().BeNull();
        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Open);

        (await _service.WithdrawOfferAsync(old.Id, _buyerId, Ct)).Refusal.Should().BeNull();
        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Withdrawn);
        old.State.Should().Be(TicketCheckoutTicketState.Valid);
    }

    [HumansFact]
    public async Task Only_the_holder_can_offer_withdraw_or_invite()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        var ticket = _repo.Tickets.Single(t => t.OrderId == order.Id);

        (await _service.OfferForResaleAsync(ticket.Id, _otherBuyerId, Ct)).Refusal.Should().Be("Tickets_Resale_NotFound");
        (await _service.OfferForResaleAsync(ticket.Id, _buyerId, Ct)).Refusal.Should().BeNull();
        (await _service.WithdrawOfferAsync(ticket.Id, _otherBuyerId, Ct)).Refusal.Should().Be("Tickets_Resale_NotFound");
        (await _service.InviteBuyerAsync(ticket.Id, _otherBuyerId, "x@example.com", new Uri("https://h.example/r/"), "en", Ct))
            .Refusal.Should().Be("Tickets_Resale_NotFound");
        (await _service.CancelInviteAsync(ticket.Id, _otherBuyerId, Ct)).Refusal.Should().Be("Tickets_Resale_NotFound");
        ticket.State.Should().Be(TicketCheckoutTicketState.Offered);
    }

    [HumansFact]
    public async Task A_directed_offer_sells_only_to_the_invited_email_through_the_link()
    {
        var old = await OfferedTicketAsync();
        EmailMessage? sent = null;
        await _email.SendAsync(Arg.Do<EmailMessage>(m => sent = m), Arg.Any<CancellationToken>());

        (await _service.InviteBuyerAsync(old.Id, _buyerId, "bo@example.com", new Uri("https://h.example/Tickets/Buy/Resale/"), "en", Ct))
            .Refusal.Should().BeNull();

        sent!.RecipientEmail.Should().Be("bo@example.com");
        var token = sent.HtmlBody["https://h.example/Tickets/Buy/Resale/".Length..];
        _repo.Offers.Single().DirectedTokenHash.Should().Be(TicketCheckoutService.HashToken(token)).And.NotBe(token);
        (await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct)).Refusal.Should().Be("Tickets_Buy_ResaleOnly");
        (await _service.StartCheckoutAsync(Buy() with { DirectedToken = "wrong" }, _otherBuyerId, Ct))
            .Refusal.Should().Be("Tickets_Resale_InviteInvalid");

        var order = await BuyAndPayAsync(Buy() with { DirectedToken = token }, _otherBuyerId);

        _repo.Offers.Single().DirectedUsedAt.Should().Be(Now);
        old.State.Should().Be(TicketCheckoutTicketState.Resold);
        _repo.Events.Should().Contain(e => e.OrderId == order.Id && e.Kind == TicketCheckoutOrderEventKind.ResaleInviteUsed);
        _repo.Events.Should().Contain(e => e.OrderId == old.OrderId && e.Kind == TicketCheckoutOrderEventKind.ResaleInvited);
    }

    [HumansFact]
    public async Task A_lapsed_invitation_rejoins_the_queue_at_its_place()
    {
        var old = await OfferedTicketAsync();
        await _service.InviteBuyerAsync(old.Id, _buyerId, "someone@example.com", new Uri("https://h.example/r/"), "en", Ct);
        (await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct)).Refusal.Should().Be("Tickets_Buy_ResaleOnly");

        _clock.Advance(Duration.FromDays(_salesEvent.DirectedResaleDays) + Duration.FromMinutes(1));
        await _service.ReconcileOpenOrdersAsync(Ct);

        _repo.Offers.Single().DirectedTokenHash.Should().BeNull();
        _repo.Events.Should().Contain(e => e.OrderId == old.OrderId && e.Kind == TicketCheckoutOrderEventKind.ResaleInviteLapsed);
        (await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct)).Refusal.Should().BeNull();
    }

    [HumansFact]
    public async Task Invitations_wait_until_resale_opens()
    {
        _salesEvent.ResaleOpensAtSold = 2;
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        var ticket = _repo.Tickets.Single(t => t.OrderId == order.Id);
        await _service.OfferForResaleAsync(ticket.Id, _buyerId, Ct);

        (await _service.InviteBuyerAsync(ticket.Id, _buyerId, "bo@example.com", new Uri("https://h.example/r/"), "en", Ct))
            .Refusal.Should().Be("Tickets_Resale_NotOpenYet");
        await _email.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
    }

    [HumansFact]
    public async Task Wave_pin_keeps_the_wave_on_sale_when_the_grant_was_issued()
    {
        TicketPrice.For(_salesEvent, new LocalDate(2027, 2, 10), [], pinnedOn: new LocalDate(2027, 1, 10))!
            .AmountMinor.Should().Be(22500);

        var pinProgram = Guid.NewGuid();
        _salesEvent.PriceRules.Add(new TicketPriceRule { GrantProgramId = pinProgram, Kind = TicketPriceRuleKind.WavePin });
        _clock.Reset(Instant.FromUtc(2027, 2, 10, 12, 0));
        _grants.GetActiveGrantsAsync(_buyerId, Arg.Any<CancellationToken>())
            .Returns([new ActiveGrantInfo(Guid.NewGuid(), pinProgram, "Low income wave", _event.Id, Now)]);

        var page = (await _service.GetBuyPagesAsync(_buyerId, Ct)).Single();

        page.OwnPrice!.AmountMinor.Should().Be(22500);
        page.PublicPrice!.AmountMinor.Should().Be(25000);
    }

    private readonly Guid _adminId = Guid.NewGuid();

    private async Task<TicketCheckoutTicket> PaidAdultAsync()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        return _repo.Tickets.Single(t => t.OrderId == order.Id && t.Kind == TicketHolderKind.Adult);
    }

    [HumansFact]
    public async Task Rename_moves_a_full_price_ticket_to_the_new_holder_and_audits_it()
    {
        var ticket = await PaidAdultAsync();
        var newHolder = Guid.NewGuid();
        _provisioning.FindOrCreateUserByEmailAsync("cy@example.com", "Cy Pérez", ContactSource.Manual, Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(newHolder, Created: true));

        (await _service.RenameTicketAsync(ticket.Id, " Cy Pérez ", "cy@example.com", "Agreed by the ticketing lead", _adminId, Ct))
            .Refusal.Should().BeNull();

        ticket.HolderUserId.Should().Be(newHolder);
        ticket.HolderName.Should().Be("Cy Pérez");
        ticket.State.Should().Be(TicketCheckoutTicketState.Valid);
        _repo.Events.Should().Contain(e => e.Kind == TicketCheckoutOrderEventKind.Renamed && e.ActorUserId == _adminId
            && !e.Detail.Contains("Cy", StringComparison.Ordinal));
        _repo.Orders.Single(o => o.Id == ticket.OrderId).Lines.Single(l => l.Kind == CheckoutLineKind.Ticket)
            .AttendeeUserId.Should().Be(newHolder);
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCheckoutTicketRenamed, Arg.Any<string>(), ticket.OrderId,
            Arg.Any<string>(), _adminId, newHolder, AuditSubjectTypes.User);
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCheckoutTicketRenamed, Arg.Any<string>(), ticket.OrderId,
            Arg.Any<string>(), _adminId, _buyerId, AuditSubjectTypes.User);
    }

    [HumansFact]
    public async Task Rename_refuses_an_unverified_address_of_someone_who_already_holds_a_ticket()
    {
        var ticket = await PaidAdultAsync();
        await BuyAndPayAsync(Buy(), _otherBuyerId);
        _provisioning.FindOrCreateUserByEmailAsync("old@example.com", "Cy", ContactSource.Manual, Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(_otherBuyerId, Created: false));

        (await _service.RenameTicketAsync(ticket.Id, "Cy", "old@example.com", "Asked nicely", _adminId, Ct))
            .Refusal.Should().Be("That person already holds a ticket for this event.");
        ticket.HolderUserId.Should().Be(_buyerId);
    }

    [HumansFact]
    public async Task Rename_needs_a_reason_within_the_limit()
    {
        var ticket = await PaidAdultAsync();

        (await _service.RenameTicketAsync(ticket.Id, "Cy", "cy@example.com", " ", _adminId, Ct))
            .Refusal.Should().StartWith("Give a reason");
        (await _service.RenameTicketAsync(ticket.Id, "Cy", "cy@example.com", new string('x', 501), _adminId, Ct))
            .Refusal.Should().StartWith("Keep the reason under");
        await _provisioning.DidNotReceiveWithAnyArgs().FindOrCreateUserByEmailAsync(default!, default!, default, default);
    }

    [HumansFact]
    public async Task Rename_to_another_person_refuses_class_priced_tickets_but_a_name_fix_is_allowed()
    {
        _grants.GetActiveGrantsAsync(_buyerId, Arg.Any<CancellationToken>())
            .Returns([new ActiveGrantInfo(Guid.NewGuid(), LowIncomeProgram, "Low income", _event.Id, Now)]);
        var ticket = await PaidAdultAsync();
        var buyer = await _users.GetUserInfoAsync(_buyerId, Ct);
        _users.GetByEmailOrAlternateAsync("ana@example.com", Arg.Any<CancellationToken>()).Returns(buyer);

        (await _service.RenameTicketAsync(ticket.Id, "Cy", "cy@example.com", "Asked nicely", _adminId, Ct))
            .Refusal.Should().StartWith("Only a full price ticket");
        ticket.HolderUserId.Should().Be(_buyerId);
        await _provisioning.DidNotReceiveWithAnyArgs().FindOrCreateUserByEmailAsync(default!, default!, default, default);

        (await _service.RenameTicketAsync(ticket.Id, "Ana María García", "ana@example.com", "Typo in the legal name", _adminId, Ct))
            .Refusal.Should().BeNull();
        ticket.HolderName.Should().Be("Ana María García");
        _repo.Orders.Single(o => o.Id == ticket.OrderId).Lines.Single(l => l.Kind == CheckoutLineKind.Ticket)
            .AttendeeName.Should().Be("Ana María García");
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCheckoutTicketRenamed, Arg.Any<string>(), ticket.OrderId,
            Arg.Any<string>(), _adminId, _buyerId, AuditSubjectTypes.User);
    }

    [HumansFact]
    public async Task Void_takes_an_offered_ticket_out_of_the_queue_with_its_dependents()
    {
        _salesEvent.ChildPriceMinor = 5000;
        var adult = await OfferedTicketAsync(Buy() with { ChildNames = ["Kid García"] });
        var child = _repo.Tickets.Single(t => t.Kind == TicketHolderKind.Child);

        (await _service.VoidTicketAsync(adult.Id, "Duplicate purchase", _adminId, Ct)).Refusal.Should().BeNull();

        adult.State.Should().Be(TicketCheckoutTicketState.Void);
        adult.VoidReason.Should().Be("Duplicate purchase");
        child.State.Should().Be(TicketCheckoutTicketState.Void);
        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Withdrawn);
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCheckoutTicketVoided, Arg.Any<string>(), adult.OrderId,
            Arg.Is<string>(d => d.Contains("withdrawing its resale offer", StringComparison.Ordinal)), _adminId,
            _buyerId, AuditSubjectTypes.User);
        (await _service.VoidTicketAsync(adult.Id, "Again", _adminId, Ct)).Succeeded.Should().BeFalse();
    }

    [HumansFact]
    public async Task Void_refuses_while_a_buyer_is_paying_for_the_offer()
    {
        var adult = await OfferedTicketAsync();
        _repo.Offers.Single().State = TicketResaleOfferState.Reserved;

        (await _service.VoidTicketAsync(adult.Id, "Fraud", _adminId, Ct)).Succeeded.Should().BeFalse();
        adult.State.Should().Be(TicketCheckoutTicketState.Offered);
    }

    [HumansFact]
    public async Task Companion_is_a_free_attached_ticket_on_its_own_paid_order_once_per_adult()
    {
        var adult = await PaidAdultAsync();
        _delivery.ClearReceivedCalls();

        (await _service.IssueCompanionAsync(adult.Id, "Helper Ruiz", null, "Wheelchair user", _adminId, Ct)).Refusal.Should().BeNull();

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

        (await _service.IssueCompanionAsync(adult.Id, "Second Helper", null, "Again", _adminId, Ct)).Succeeded.Should().BeFalse();
    }

    [HumansFact]
    public async Task Void_refuses_a_child_while_its_adult_waits_in_the_queue()
    {
        _salesEvent.ChildPriceMinor = 5000;
        await OfferedTicketAsync(Buy() with { ChildNames = ["Kid García"] });
        var child = _repo.Tickets.Single(t => t.Kind == TicketHolderKind.Child);

        (await _service.VoidTicketAsync(child.Id, "Not coming", _adminId, Ct))
            .Refusal.Should().StartWith("The adult's ticket is in the resale queue");
        child.State.Should().Be(TicketCheckoutTicketState.Valid);
    }

    [HumansFact]
    public async Task A_companion_on_its_own_order_is_voided_when_its_adult_resells()
    {
        var adult = await PaidAdultAsync();
        (await _service.IssueCompanionAsync(adult.Id, "Helper Ruiz", null, "Wheelchair user", _adminId, Ct)).Refusal.Should().BeNull();
        var companion = _repo.Tickets.Single(t => t.Kind == TicketHolderKind.Companion);
        (await _service.OfferForResaleAsync(adult.Id, _buyerId, Ct)).Refusal.Should().BeNull();
        _salesEvent.Mode = TicketSalesMode.ResaleOnly;

        await BuyAndPayAsync(Buy(), _otherBuyerId);

        adult.State.Should().Be(TicketCheckoutTicketState.Resold);
        companion.State.Should().Be(TicketCheckoutTicketState.Void);
        _repo.Events.Should().Contain(e => e.OrderId == companion.OrderId && e.Kind == TicketCheckoutOrderEventKind.Voided);
    }

    [HumansFact]
    public async Task Companion_refuses_someone_who_already_holds_a_ticket()
    {
        var adult = await PaidAdultAsync();
        _provisioning.FindOrCreateUserByEmailAsync("ana@example.com", "Ana", ContactSource.Manual, Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(_buyerId, Created: false));

        (await _service.IssueCompanionAsync(adult.Id, "Ana", "ana@example.com", "Helper", _adminId, Ct))
            .Refusal.Should().Be("That person already holds a ticket for this event.");
    }

    [HumansFact]
    public async Task Companion_respects_capacity()
    {
        var adult = await PaidAdultAsync();
        _salesEvent.Capacity = 1;

        (await _service.IssueCompanionAsync(adult.Id, "Helper Ruiz", null, "Wheelchair user", _adminId, Ct))
            .Refusal.Should().Contain("capacity");
        _repo.Tickets.Should().ContainSingle();
    }

    [HumansFact]
    public async Task A_lapsed_hold_is_closed_at_the_backend_and_a_late_payment_still_issues_flagged_tickets()
    {
        var offerTicket = await OfferedTicketAsync();
        var started = await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct);
        var order = _repo.Orders.Single(o => o.Id == started.OrderId);
        _backend.GetStatusAsync(order.BackendOrderId!, Arg.Any<CancellationToken>())
            .Returns(new CheckoutStatus(CheckoutState.Pending, null, null, null));

        _clock.Advance(Duration.FromHours(2));
        await _service.ReconcileOpenOrdersAsync(Ct);

        await _backend.Received(1).CancelAsync(order.BackendOrderId!, Arg.Any<CancellationToken>());
        order.State.Should().Be(TicketCheckoutOrderState.Expired);
        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Open);

        _backend.GetStatusAsync(order.BackendOrderId!, Arg.Any<CancellationToken>())
            .Returns(new CheckoutStatus(CheckoutState.Paid, 100, "ideal", _clock.GetCurrentInstant()));
        await _service.CheckoutChangedAsync("test", order.BackendOrderId!, Ct);

        order.State.Should().Be(TicketCheckoutOrderState.Paid);
        _repo.Tickets.Should().Contain(t => t.OrderId == order.Id && t.HolderUserId == _otherBuyerId);
        offerTicket.State.Should().Be(TicketCheckoutTicketState.Resold);
        _repo.Events.Should().Contain(e => e.OrderId == order.Id && e.Detail.Contains("after the order was Expired"));
    }

    [HumansFact]
    public async Task A_late_payment_whose_offer_went_to_another_buyer_leaves_that_sale_alone()
    {
        var offerTicket = await OfferedTicketAsync();
        var first = await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct);
        var late = _repo.Orders.Single(o => o.Id == first.OrderId);
        _backend.GetStatusAsync(late.BackendOrderId!, Arg.Any<CancellationToken>())
            .Returns(new CheckoutStatus(CheckoutState.Expired, null, null, null));
        await _service.CheckoutChangedAsync("test", late.BackendOrderId!, Ct);
        var thirdId = Guid.NewGuid();
        var third = new User { Id = thirdId, BurnerName = "Kite", FirstName = "Cy", LastName = "Doe", Email = "cy@example.com" };
        _users.GetUserInfoAsync(thirdId, Arg.Any<CancellationToken>()).Returns(UserInfo.Create(third, [], [], UserFixtures.Profile()));
        var winner = await _service.StartCheckoutAsync(Buy(), thirdId, Ct);
        _repo.Offers.Single().ReservedByOrderId.Should().Be(winner.OrderId);

        _backend.GetStatusAsync(late.BackendOrderId!, Arg.Any<CancellationToken>())
            .Returns(new CheckoutStatus(CheckoutState.Paid, null, "card", Now));
        await _service.CheckoutChangedAsync("test", late.BackendOrderId!, Ct);

        late.State.Should().Be(TicketCheckoutOrderState.Paid);
        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Reserved);
        offerTicket.State.Should().Be(TicketCheckoutTicketState.Offered);
        await _backend.DidNotReceive().RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Pending_refunds_settle_from_the_refunded_total_and_a_reported_failure_frees_the_amount()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        _backend.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Pending, "re_p", null));
        await _service.RefundAsync(order.Id, 10000, "first", Guid.NewGuid(), Ct);
        await _service.RefundAsync(order.Id, 5000, "second", Guid.NewGuid(), Ct);
        _repo.Refunds.Select(r => r.Refund.State).Should().AllBeEquivalentTo(RefundState.Pending);

        _backend.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new CheckoutStatus(CheckoutState.Paid, 0, "card", Now, RefundedMinor: 10000, RefundFailed: true));
        _backend.RefundAsync(Arg.Is<RefundRequest>(r => r.AmountMinor == 5000), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Failed, "re_p", "bank refused"));
        await _service.ReconcileOpenOrdersAsync(Ct);

        _repo.Refunds.Select(r => r.Refund.State).Should().Equal(RefundState.Succeeded, RefundState.Failed);
        (await _service.GetOrderDetailAsync(order.Id, Ct))!.RefundableMinor.Should().Be(12500);
    }

    [HumansFact]
    public async Task Erasure_reaches_the_backend_first_and_a_backend_failure_leaves_everything_for_the_retry()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        await _service.EraseForUserAsync(_buyerId, Ct);
        await _backend.Received(1).ErasePersonalDataAsync("cs_1", "ana@example.com", Arg.Any<CancellationToken>());

        _backend.ErasePersonalDataAsync(default!, default!, default).ReturnsForAnyArgs<Task>(_ => throw new TicketCheckoutException("down"));
        var act = () => _service.EraseForUserAsync(_buyerId, Ct);
        await act.Should().ThrowAsync<TicketCheckoutException>();
        _repo.Erased.Should().Be(1);
    }

    [HumansFact]
    public async Task A_returned_ticket_sells_only_at_the_public_price_so_a_class_price_buyer_takes_fresh_stock_while_there_is_any()
    {
        await OfferedTicketAsync();
        _salesEvent.Mode = TicketSalesMode.Open;
        _grants.GetActiveGrantsAsync(_otherBuyerId, Arg.Any<CancellationToken>())
            .Returns([new ActiveGrantInfo(Guid.NewGuid(), LowIncomeProgram, "Low income", _event.Id, Now)]);

        var fresh = await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct);

        var line = _repo.Orders.Single(o => o.Id == fresh.OrderId).Lines.Single();
        line.ResaleOfferId.Should().BeNull();
        line.AmountMinor.Should().Be(12500);
        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Open);
    }

    [HumansFact]
    public async Task In_resale_only_mode_every_buyer_pays_the_public_price_less_credits_and_the_refund_never_exceeds_what_came_in_plus_credits()
    {
        var old = await OfferedTicketAsync();
        _grants.GetActiveGrantsAsync(_otherBuyerId, Arg.Any<CancellationToken>())
            .Returns([
                new ActiveGrantInfo(Guid.NewGuid(), LowIncomeProgram, "Low income", _event.Id, Now),
                new ActiveGrantInfo(Guid.NewGuid(), CreditProgram, "Credit", _event.Id, Now),
            ]);
        (await _service.GetBuyPagesAsync(_otherBuyerId, Ct)).Single().OwnPrice!.AmountMinor.Should().Be(17500);

        var order = await BuyAndPayAsync(Buy(), _otherBuyerId);

        var line = order.Lines.Single();
        line.ResaleOfferId.Should().NotBeNull();
        line.AmountMinor.Should().Be(17500);
        line.ClassLabel.Should().BeNull();
        var refunded = _repo.Refunds.Single().Refund.AmountMinor;
        refunded.Should().Be(old.PricePaidMinor);
        refunded.Should().BeLessThanOrEqualTo(line.AmountMinor + line.CreditsMinor);
    }

    [HumansFact]
    public async Task A_caller_that_loses_the_paid_claim_issues_nothing()
    {
        await _service.StartCheckoutAsync(Buy(), _buyerId, Ct);
        _repo.ClaimedPaid.Add(_repo.Orders.Single().Id);

        await PayAsync("cs_1");

        _repo.Tickets.Should().BeEmpty();
        await _delivery.DidNotReceiveWithAnyArgs().TicketsIssuedAsync(default, default);
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
    public async Task Reconcile_hands_tickets_to_delivery_again_when_no_email_was_recorded()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        _delivery.ClearReceivedCalls();

        await _service.ReconcileOpenOrdersAsync(Ct);
        await _delivery.DidNotReceiveWithAnyArgs().TicketsIssuedAsync(default, default);

        _clock.Advance(Duration.FromMinutes(31));
        await _service.ReconcileOpenOrdersAsync(Ct);
        await _delivery.Received(1).TicketsIssuedAsync(order.Id, Arg.Any<CancellationToken>());

        await _service.RecordDeliveryAsync(order.Id, TicketCheckoutOrderEventKind.TicketsEmailed, "sent", null, Ct);
        _delivery.ClearReceivedCalls();
        await _service.ReconcileOpenOrdersAsync(Ct);
        await _delivery.DidNotReceiveWithAnyArgs().TicketsIssuedAsync(default, default);
    }

    [HumansFact]
    public async Task A_resale_refund_that_cannot_start_is_recorded_as_failed_under_the_offer_id()
    {
        var old = await OfferedTicketAsync();
        (await _service.RefundAsync(old.OrderId, 22500, "already refunded", Guid.NewGuid(), Ct)).Succeeded.Should().BeTrue();

        await BuyAndPayAsync(Buy(), _otherBuyerId);

        var offer = _repo.Offers.Single();
        offer.RefundId.Should().Be(offer.Id);
        var failed = _repo.Refunds.Single(r => r.RefundId == offer.Id).Refund;
        failed.State.Should().Be(RefundState.Failed);
        failed.AmountMinor.Should().Be(22500);
        _repo.Events.Should().Contain(e => e.OrderId == old.OrderId && e.Detail.Contains("refund it by hand", StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task A_resale_refund_whose_backend_call_threw_is_retried_under_the_same_id()
    {
        var old = await OfferedTicketAsync();
        _backend.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns<RefundResult>(_ => throw new TicketCheckoutException("timeout"));

        await BuyAndPayAsync(Buy(), _otherBuyerId);

        var offer = _repo.Offers.Single();
        var refund = _repo.Refunds.Single().Refund;
        refund.Id.Should().Be(offer.Id);
        refund.State.Should().Be(RefundState.Pending);

        _backend.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Succeeded, "re_9", null));
        await _service.ReconcileOpenOrdersAsync(Ct);

        _repo.Refunds.Should().ContainSingle();
        refund.State.Should().Be(RefundState.Succeeded);
        await _backend.Received(2).RefundAsync(
            Arg.Is<RefundRequest>(r => r.RefundId == offer.Id && r.BackendOrderId == "cs_1"), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task A_refund_call_that_throws_stays_pending_so_the_amount_stays_held()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        _backend.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns<RefundResult>(_ => throw new TicketCheckoutException("timeout"));

        (await _service.RefundAsync(order.Id, 10000, "timeout", Guid.NewGuid(), Ct)).Succeeded.Should().BeTrue();

        _repo.Refunds.Single().Refund.State.Should().Be(RefundState.Pending);
        (await _service.GetOrderDetailAsync(order.Id, Ct))!.RefundableMinor.Should().Be(12500);
        var rows = _repo.Events.Count;
        await _service.ReconcileOpenOrdersAsync(Ct);
        _repo.Events.Should().HaveCount(rows, "a resend that changes nothing adds no rows");

        _backend.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Succeeded, "re_5", null));
        await _service.ReconcileOpenOrdersAsync(Ct);

        _repo.Refunds.Single().Refund.State.Should().Be(RefundState.Succeeded);
        await _backend.Received(3).RefundAsync(
            Arg.Is<RefundRequest>(r => r.RefundId == _repo.Refunds[0].RefundId), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task An_earlier_failed_refund_does_not_fail_a_later_one_still_in_flight()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        _backend.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new CheckoutStatus(CheckoutState.Paid, 0, "card", Now, RefundedMinor: 0, RefundFailed: true));
        _backend.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Failed, "re_a", "bank refused"));
        await _service.RefundAsync(order.Id, 10000, "first", Guid.NewGuid(), Ct);
        _backend.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Pending, "re_b", null));

        await _service.RefundAsync(order.Id, 5000, "second", Guid.NewGuid(), Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);

        _repo.Refunds.Select(r => r.Refund.State).Should().Equal(RefundState.Failed, RefundState.Pending);

        var second = _repo.Refunds[1].RefundId;
        _backend.RefundAsync(Arg.Is<RefundRequest>(r => r.RefundId == second), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Failed, "re_b", "bank refused"));
        await _service.ReconcileOpenOrdersAsync(Ct);

        _repo.Refunds[1].Refund.State.Should().Be(RefundState.Failed);
    }

    [HumansFact]
    public async Task An_unpaid_partner_is_forgotten_once_a_late_payment_can_no_longer_arrive()
    {
        var result = await _service.StartCheckoutAsync(Buy(partnerName: "Bo Smith", partnerEmail: "bo@example.com"), _buyerId, Ct);
        _backend.GetStatusAsync("cs_1", Arg.Any<CancellationToken>())
            .Returns(new CheckoutStatus(CheckoutState.Pending, null, null, null));
        await _service.CancelAsync(result.OrderId!.Value, _buyerId, Ct);
        var partner = _repo.Orders.Single().Lines[1];

        await _service.ReconcileOpenOrdersAsync(Ct);
        partner.AttendeeEmail.Should().Be("bo@example.com");

        _clock.Advance(Duration.FromDays(31));
        await _service.ReconcileOpenOrdersAsync(Ct);

        partner.AttendeeEmail.Should().BeNull();
        partner.AttendeeName.Should().NotBe("Bo Smith");
    }

    [HumansFact]
    public async Task One_invitation_at_a_time_and_at_most_three_per_ticket()
    {
        var old = await OfferedTicketAsync();
        var link = new Uri("https://h.example/r/");

        (await _service.InviteBuyerAsync(old.Id, _buyerId, "a@example.com", link, "en", Ct)).Refusal.Should().BeNull();
        (await _service.InviteBuyerAsync(old.Id, _buyerId, "b@example.com", link, "en", Ct)).Refusal.Should().Be("Tickets_Resale_InviteLive");
        for (var i = 0; i < 2; i++)
        {
            (await _service.CancelInviteAsync(old.Id, _buyerId, Ct)).Refusal.Should().BeNull();
            (await _service.InviteBuyerAsync(old.Id, _buyerId, $"c{i}@example.com", link, "en", Ct)).Refusal.Should().BeNull();
        }
        await _service.CancelInviteAsync(old.Id, _buyerId, Ct);

        (await _service.InviteBuyerAsync(old.Id, _buyerId, "d@example.com", link, "en", Ct)).Refusal.Should().Be("Tickets_Resale_InviteLimit");
        _repo.Offers.Single().DirectedEmail.Should().BeNull();

        (await _service.WithdrawOfferAsync(old.Id, _buyerId, Ct)).Refusal.Should().BeNull();
        (await _service.OfferForResaleAsync(old.Id, _buyerId, Ct)).Refusal.Should().BeNull();
        (await _service.InviteBuyerAsync(old.Id, _buyerId, "e@example.com", link, "en", Ct)).Refusal.Should().Be("Tickets_Resale_InviteLimit");
    }

    [HumansFact]
    public async Task Erasure_voids_the_holders_children_and_passes_every_address_to_the_repository()
    {
        _salesEvent.ChildPriceMinor = 5000;
        await BuyAndPayAsync(Buy() with { ChildNames = ["Kid García"] }, _buyerId);

        await _service.EraseForUserAsync(_buyerId, Ct);

        _repo.Tickets.Should().OnlyContain(t => t.State == TicketCheckoutTicketState.Void);
        _repo.ErasedAddresses.Should().Contain("ana@example.com");
    }

    [HumansFact]
    public async Task A_partners_export_shows_their_own_line_but_not_who_paid()
    {
        var partnerId = Guid.NewGuid();
        _provisioning.FindOrCreateUserByEmailAsync("bo@example.com", null, ContactSource.Manual, Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(partnerId, Created: true));
        await _service.StartCheckoutAsync(Buy(partnerName: "Bo Smith", partnerEmail: "bo@example.com", supporter: 100000), _buyerId, Ct);
        await PayAsync("cs_1");

        var json = System.Text.Json.JsonSerializer.Serialize(
            (await _service.ContributeForUserAsync(partnerId, Ct)).Select(slice => slice.Data));

        json.Should().Contain("Bo Smith").And.NotContain("ana@example.com").And.NotContain("Ana García");
    }

    [HumansFact]
    public async Task At_sell_out_a_class_price_buyer_takes_a_returned_ticket_at_the_public_price()
    {
        await OfferedTicketAsync();
        _salesEvent.Mode = TicketSalesMode.Open;
        _salesEvent.Capacity = 1;
        _grants.GetActiveGrantsAsync(_otherBuyerId, Arg.Any<CancellationToken>())
            .Returns([new ActiveGrantInfo(Guid.NewGuid(), LowIncomeProgram, "Low income", _event.Id, Now)]);

        var result = await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct);

        result.Refusal.Should().BeNull();
        var line = _repo.Orders.Single(o => o.Id == result.OrderId).Lines.Single();
        line.ResaleOfferId.Should().Be(_repo.Offers.Single().Id);
        line.AmountMinor.Should().Be(22500);
    }

    [HumansFact]
    public async Task My_tickets_lists_the_valid_children_under_their_adult_and_never_offers_them()
    {
        _salesEvent.ChildPriceMinor = 5000;
        await BuyAndPayAsync(Buy() with { ChildNames = ["Kid García"] }, _buyerId);

        var held = await _service.GetHeldTicketsAsync(_buyerId, Ct);

        held.Select(h => h.Ticket.Kind).Should().Equal(TicketHolderKind.Adult, TicketHolderKind.Child);
        held[1].CanOffer.Should().BeFalse();
        held[0].HasDependents.Should().BeTrue();
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
        _salesEvent.PriceRules.Add(new TicketPriceRule { GrantProgramId = Guid.NewGuid(), Kind = TicketPriceRuleKind.Credit, AmountMinor = 9000 });
        var closed = new TicketSalesEvent
        {
            Id = Guid.NewGuid(),
            EventSettingsId = _event.Id,
            BackendKey = "test",
            Mode = TicketSalesMode.Closed,
            Waves = [new TicketSalesWave { Name = "Only", EndsOn = new LocalDate(2027, 8, 1), PriceMinor = 1000 }],
        };
        _repo.SalesEvents.Add(closed);

        var sales = await _service.GetPublicSalesAsync(Ct);

        sales.Should().ContainSingle().Which.Should().Be(new PublicSale("Nowhere 2027", 22500, ResaleOnly: false));
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
            Task.FromResult(Offers.FirstOrDefault(o => string.Equals(o.DirectedTokenHash, tokenHash, StringComparison.Ordinal)));
        public Task<IReadOnlyList<TicketResaleOffer>> GetOffersForSalesEventAsync(Guid salesEventId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TicketResaleOffer>>(Offers.Where(o => o.SalesEventId == salesEventId).ToList());
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

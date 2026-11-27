using AwesomeAssertions;
using Hangfire;
using Humans.AuditLog.Contracts;
using Humans.Base.Configuration;
using Humans.Email.Contracts;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using Humans.Tickets.Services.Delivery;
using Humans.Tickets.Tests.Infrastructure;
using Humans.Users.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;

namespace Humans.Tickets.Tests.Services.Delivery;

public sealed class TicketDeliveryServiceTests
{
    private readonly ITicketCheckoutService _checkout = Substitute.For<ITicketCheckoutService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IEmailService _email = Substitute.For<IEmailService>();
    private readonly IBackgroundJobClient _jobs = Substitute.For<IBackgroundJobClient>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly List<EmailMessage> _sent = [];

    private static readonly Guid BuyerId = Guid.NewGuid();
    private static readonly Guid PartnerId = Guid.NewGuid();
    private readonly TicketingSettings _ticketing = new() { Id = Guid.NewGuid(), EventSettingsId = Guid.NewGuid() };
    private readonly TicketCheckoutOrder _order;

    public TicketDeliveryServiceTests()
    {
        _order = new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = _ticketing.Id,
            BuyerUserId = BuyerId,
            BuyerName = "Ana Buyer",
            BuyerEmail = "ana@example.com",
            State = TicketCheckoutOrderState.Paid,
            PaidAt = Instant.FromUtc(2027, 1, 2, 9, 0),
            TotalMinor = 45000,
            Lines =
            [
                new TicketCheckoutOrderLine { Kind = OrderLineKind.Ticket, AmountMinor = 22500, VatRate = 0.10m, HolderName = "Ana Buyer" },
                new TicketCheckoutOrderLine { Kind = OrderLineKind.Ticket, AmountMinor = 22500, VatRate = 0.10m, HolderName = "Ben Partner" },
            ],
            Culture = "es",
        };
        Tickets();
        _checkout.GetTicketingSettingsAsync(_ticketing.Id, Arg.Any<CancellationToken>()).Returns(_ticketing);
        _settings.GetEventSettingsByIdAsync(_ticketing.EventSettingsId, Arg.Any<CancellationToken>())
            .Returns(new EventSettingsInfo(_ticketing.EventSettingsId, "Nowhere 2027", 2027, "Europe/Madrid",
                new LocalDate(2027, 6, 1), 0, 5, 7, new Dictionary<int, int>(), null, null));
        _users.GetUserInfoAsync(PartnerId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(
            new UserInfo(PartnerId, "Ben", false, "de", null, Instant.MinValue, Instant.MinValue, null, null, null, null, null, null, null,
                "ben@example.com", [], [], null)));
        _users.GetUserInfoAsync(BuyerId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(
            new UserInfo(BuyerId, "Ana", false, "es", null, Instant.MinValue, Instant.MinValue, null, null, null, null, null, null, null,
                "ana@example.com", [], [], null)));
        _email.SendAsync(Arg.Do<EmailMessage>(_sent.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
    }

    private TicketDeliveryService Create() => new(
        _checkout, _settings, _users, _email, TestTicketsEmails.Create(), TestTicketCodeImages.Create(), _jobs, _audit,
        Options.Create(new EmailSettings { BaseUrl = TestTicketsEmails.BaseUrl }), NullLogger<TicketDeliveryService>.Instance);

    private TicketCheckoutTicket Ticket(Guid? holderId, string name, string? email, TicketCheckoutTicketState state = TicketCheckoutTicketState.Valid,
        TicketHolderKind kind = TicketHolderKind.Adult, Guid? parent = null, Instant? issuedAt = null, Guid? orderId = null) => new()
        {
            Id = Guid.NewGuid(),
            ParentTicketId = parent,
            IssuedAt = issuedAt ?? Instant.MinValue,
            OrderId = orderId ?? _order.Id,
            TicketingSettingsId = _ticketing.Id,
            HolderUserId = holderId,
            HolderName = name,
            HolderEmail = email,
            Kind = kind,
            State = state,
            Barcode = "H" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant(),
        };

    private void Tickets(params TicketCheckoutTicket[] tickets)
    {
        _checkout.GetOrderDetailAsync(_order.Id, Arg.Any<CancellationToken>()).Returns(_ => new OrderDetail(_order, [], [], tickets));
        _checkout.GetTicketsForSettingsAsync(_ticketing.Id, Arg.Any<CancellationToken>()).Returns(tickets);
    }

    [HumansTheory]
    [Xunit.InlineData("rename", null)]
    [Xunit.InlineData("rename", UserState.Deleted)]
    [Xunit.InlineData("rename", UserState.Merged)]
    [Xunit.InlineData("sold", UserState.Deleted)]
    [Xunit.InlineData("unsold", UserState.Deleted)]
    public async Task SendHolderNoticeAsync_UnavailableHolder_Skips(string kind, UserState? state)
    {
        var ticket = Ticket(PartnerId, "Ben Partner", "snapshot@example.com",
            string.Equals(kind, "sold", StringComparison.Ordinal) ? TicketCheckoutTicketState.Resold
                : string.Equals(kind, "unsold", StringComparison.Ordinal) ? TicketCheckoutTicketState.Offered : TicketCheckoutTicketState.Valid);
        Tickets(ticket);
        _checkout.GetTicketAsync(ticket.Id, Arg.Any<CancellationToken>()).Returns(ticket);
        var holder = Xunit.Assert.IsType<UserInfo>(await _users.GetUserInfoAsync(PartnerId));
        _users.GetUserInfoAsync(PartnerId, Arg.Any<CancellationToken>()).Returns(state is { } value ? holder with { State = value } : null);
        var service = Create();

        await (kind switch
        {
            "rename" => service.SendRenamedTicketAsync(ticket.Id, Guid.NewGuid()),
            "sold" => service.SendResaleSoldAsync(ticket.Id, 22500),
            "unsold" => service.SendResaleUnsoldAsync(ticket.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        });

        _sent.Should().BeEmpty();
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData(UserState.Deleted)]
    [Xunit.InlineData(UserState.Merged)]
    public async Task DeliverAsync_UnavailableHolder_StillSendsReceipt(UserState? state)
    {
        var adult = Ticket(PartnerId, "Ben Partner", "snapshot@example.com");
        Tickets(adult, Ticket(null, "Dependent", null, kind: TicketHolderKind.Child, parent: adult.Id));
        var holder = Xunit.Assert.IsType<UserInfo>(await _users.GetUserInfoAsync(PartnerId));
        _users.GetUserInfoAsync(PartnerId, Arg.Any<CancellationToken>()).Returns(state is { } value ? holder with { State = value } : null);

        await Create().DeliverAsync(_order.Id);

        _sent.Should().ContainSingle().Which.TemplateName.Should().Be("ticket_order_receipt");
        _jobs.DidNotReceiveWithAnyArgs().Create(default!, default!);
        await _checkout.Received(1).RecordDeliveryAsync(_order.Id, TicketCheckoutOrderEventKind.TicketMailSkipped,
            Arg.Any<string>(), null, Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData(UserState.Deleted)]
    [Xunit.InlineData(UserState.Merged)]
    public async Task DeliverAsync_UnavailableBuyer_StillSendsOtherHolderTicket(UserState? state)
    {
        Tickets(Ticket(BuyerId, "Ana Buyer", "snapshot@example.com"), Ticket(PartnerId, "Ben Partner", "ben@example.com"));
        var buyer = Xunit.Assert.IsType<UserInfo>(await _users.GetUserInfoAsync(BuyerId));
        _users.GetUserInfoAsync(BuyerId, Arg.Any<CancellationToken>()).Returns(state is { } value ? buyer with { State = value } : null);

        await Create().DeliverAsync(_order.Id);

        var mail = _sent.Should().ContainSingle().Which;
        mail.TemplateName.Should().Be("ticket_issued");
        mail.UserId.Should().Be(PartnerId);
        mail.HtmlBody.Should().NotContain("Ana Buyer");
        _jobs.ReceivedWithAnyArgs(1).Create(default!, default!);
        await _checkout.Received(1).RecordDeliveryAsync(_order.Id, TicketCheckoutOrderEventKind.TicketMailSkipped,
            Arg.Any<string>(), null, Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task DeliverAsync_NoAvailableRecipient_RecordsSkip(bool resend)
    {
        Tickets(Ticket(BuyerId, "Ana Buyer", "snapshot@example.com"));
        _users.GetUserInfoAsync(BuyerId, Arg.Any<CancellationToken>()).Returns((UserInfo?)null);
        var service = Create();

        if (resend)
            (await service.ResendAsync(_order.Id, Guid.NewGuid())).Succeeded.Should().BeFalse();
        else
            await service.DeliverAsync(_order.Id);

        _sent.Should().BeEmpty();
        _jobs.DidNotReceiveWithAnyArgs().Create(default!, default!);
        _audit.ReceivedCalls().Should().BeEmpty();
        await _checkout.Received(1).RecordDeliveryAsync(_order.Id, TicketCheckoutOrderEventKind.TicketMailSkipped,
            Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await _checkout.DidNotReceive().RecordDeliveryAsync(_order.Id, TicketCheckoutOrderEventKind.TicketsEmailed,
            Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task SendNoticeAsync_MergedRecipient_UsesCanonicalIdentity(bool payment)
    {
        var id = payment ? BuyerId : PartnerId;
        var holder = Xunit.Assert.IsType<UserInfo>(await _users.GetUserInfoAsync(id));
        var canonical = Guid.NewGuid();
        _users.GetUserInfoAsync(id, Arg.Any<CancellationToken>()).Returns(holder with { Id = canonical, IdentityEmailColumn = "survivor@example.com" });
        var ticket = Ticket(PartnerId, "Ben Partner", "snapshot@example.com");
        Tickets(ticket);
        _checkout.GetTicketAsync(ticket.Id, Arg.Any<CancellationToken>()).Returns(ticket);
        var service = Create();
        if (payment)
        {
            _order.State = TicketCheckoutOrderState.Failed;
            await service.SendPaymentEndedAsync(_order.Id);
        }
        else
            await service.SendRenamedTicketAsync(ticket.Id, Guid.NewGuid());

        var mail = _sent.Should().ContainSingle().Which;
        mail.UserId.Should().Be(canonical);
        mail.RecipientEmail.Should().Be("survivor@example.com");
    }

    [HumansFact]
    public async Task SendPaymentEndedAsync_UnavailableBuyer_Skips()
    {
        var buyer = Xunit.Assert.IsType<UserInfo>(await _users.GetUserInfoAsync(BuyerId));
        _users.GetUserInfoAsync(BuyerId, Arg.Any<CancellationToken>()).Returns(buyer with { State = UserState.Deleted });
        _order.State = TicketCheckoutOrderState.Failed;

        await Create().SendPaymentEndedAsync(_order.Id);

        _sent.Should().BeEmpty();
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task DeliverAsync_MergedRecipients_UsesCanonicalIdentity()
    {
        var buyer = Xunit.Assert.IsType<UserInfo>(await _users.GetUserInfoAsync(BuyerId));
        var holder = Xunit.Assert.IsType<UserInfo>(await _users.GetUserInfoAsync(PartnerId));
        var buyerId = Guid.NewGuid();
        var holderId = Guid.NewGuid();
        _users.GetUserInfoAsync(BuyerId, Arg.Any<CancellationToken>()).Returns(buyer with { Id = buyerId });
        _users.GetUserInfoAsync(PartnerId, Arg.Any<CancellationToken>()).Returns(holder with { Id = holderId });
        Tickets(Ticket(BuyerId, "Ana Buyer", "ana@example.com"), Ticket(PartnerId, "Ben Partner", "ben@example.com"));

        await Create().DeliverAsync(_order.Id);

        _sent.Should().HaveCount(3);
        _sent.Where(m => string.Equals(m.RecipientEmail, "ana@example.com", StringComparison.Ordinal)).Should().OnlyContain(m => m.UserId == buyerId);
        _sent.Single(m => string.Equals(m.RecipientEmail, "ben@example.com", StringComparison.Ordinal)).UserId.Should().Be(holderId);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task SendNoticeAsync_ErasedTicket_Skips(bool rename)
    {
        var ticket = Ticket(PartnerId, TicketPiiTombstone.Name, "ben@example.com");
        Tickets(ticket);
        _checkout.GetTicketAsync(ticket.Id, Arg.Any<CancellationToken>()).Returns(ticket);
        var service = Create();

        if (rename)
            await service.SendRenamedTicketAsync(ticket.Id, Guid.NewGuid());
        else
            await service.SendJoinInvitationAsync(_order.Id, ticket.Id);

        _sent.Should().BeEmpty();
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task DeliverAsync_ErasedAdult_SkipsFamily()
    {
        var adult = Ticket(PartnerId, TicketPiiTombstone.Name, "ben@example.com");
        Tickets(adult, Ticket(null, "Child", null, kind: TicketHolderKind.Child, parent: adult.Id));

        await Create().DeliverAsync(_order.Id);

        _sent.Should().ContainSingle().Which.TemplateName.Should().Be("ticket_order_receipt");
        _jobs.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [HumansFact]
    public async Task DeliverAsync_AnonymousEmailOnly_SendsTicket()
    {
        Tickets(Ticket(null, "Guest", "guest@example.com"));

        await Create().DeliverAsync(_order.Id);

        _sent.Should().HaveCount(2);
        var mail = _sent.Single(m => string.Equals(m.TemplateName, "ticket_issued", StringComparison.Ordinal));
        mail.UserId.Should().BeNull();
        mail.RecipientEmail.Should().Be("guest@example.com");
    }

    [HumansFact]
    public async Task DeliverAsync_ErasedBuyerSnapshot_StillSendsOtherHolderTicket()
    {
        Tickets(Ticket(PartnerId, "Ben Partner", "ben@example.com"));
        _order.BuyerName = TicketPiiTombstone.Name;
        _order.BuyerEmail = TicketPiiTombstone.EmailFor(BuyerId);

        await Create().DeliverAsync(_order.Id);

        var mail = _sent.Should().ContainSingle().Which;
        mail.TemplateName.Should().Be("ticket_issued");
        mail.HtmlBody.Should().NotContain(TicketPiiTombstone.Name);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task DeliverAsync_CancelledEvent_SendsReceiptOnly(bool resend)
    {
        var adult = Ticket(PartnerId, "Ben Partner", "ben@example.com");
        var child = Ticket(null, "Child", null, kind: TicketHolderKind.Child, parent: adult.Id);
        var companion = Ticket(null, "Companion", null, kind: TicketHolderKind.Companion, parent: adult.Id);
        Tickets(adult, child, companion);
        _ticketing.Mode = TicketSalesMode.Closed;
        _ticketing.CancelledAt = Instant.FromUtc(2027, 1, 3, 0, 0);
        var service = Create();

        if (resend)
            (await service.ResendAsync(_order.Id, Guid.NewGuid())).Succeeded.Should().BeTrue();
        else
            await service.DeliverAsync(_order.Id);

        _sent.Should().ContainSingle().Which.TemplateName.Should().Be("ticket_order_receipt");
        _jobs.DidNotReceiveWithAnyArgs().Create(default!, default!);
        adult.State.Should().Be(TicketCheckoutTicketState.Valid);
        child.State.Should().Be(TicketCheckoutTicketState.Valid);
        companion.State.Should().Be(TicketCheckoutTicketState.Valid);
    }

    [HumansTheory]
    [Xunit.InlineData("join")]
    [Xunit.InlineData("rename")]
    [Xunit.InlineData("sold")]
    [Xunit.InlineData("unsold")]
    [Xunit.InlineData("payment")]
    public async Task SendNoticeAsync_CancelledEvent_SuppressesStalePromises(string kind)
    {
        var ticket = Ticket(PartnerId, "Ben Partner", "ben@example.com",
            string.Equals(kind, "sold", StringComparison.Ordinal) ? TicketCheckoutTicketState.Resold
                : string.Equals(kind, "unsold", StringComparison.Ordinal) ? TicketCheckoutTicketState.Offered : TicketCheckoutTicketState.Valid);
        Tickets(ticket);
        _checkout.GetTicketAsync(ticket.Id, Arg.Any<CancellationToken>()).Returns(ticket);
        _ticketing.Mode = TicketSalesMode.Closed;
        _ticketing.CancelledAt = Instant.FromUtc(2027, 1, 3, 0, 0);
        if (string.Equals(kind, "payment", StringComparison.Ordinal))
            _order.State = TicketCheckoutOrderState.Failed;
        var service = Create();

        await (kind switch
        {
            "join" => service.SendJoinInvitationAsync(_order.Id, ticket.Id),
            "rename" => service.SendRenamedTicketAsync(ticket.Id, Guid.NewGuid()),
            "sold" => service.SendResaleSoldAsync(ticket.Id, 22500),
            "unsold" => service.SendResaleUnsoldAsync(ticket.Id),
            "payment" => service.SendPaymentEndedAsync(_order.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        });

        _sent.Should().BeEmpty();
        _audit.ReceivedCalls().Should().BeEmpty();
        await _checkout.DidNotReceive().RecordDeliveryAsync(Arg.Any<Guid>(), Arg.Any<TicketCheckoutOrderEventKind>(),
            Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>());
    }

    [HumansFact]
    public async Task Deliver_RendersTheHolderFirstAndDependentsByIssueTime()
    {
        var now = Instant.FromUtc(2027, 1, 1, 12, 0);
        var adult = Ticket(BuyerId, "Ana Buyer", "ana@example.com", issuedAt: now + Duration.FromDays(2));
        var older = Ticket(null, "Older child", null, kind: TicketHolderKind.Child, parent: adult.Id, issuedAt: now);
        var newer = Ticket(null, "Newer child", null, kind: TicketHolderKind.Child, parent: adult.Id, issuedAt: now + Duration.FromDays(1));
        Tickets(newer, adult, older);

        await Create().DeliverAsync(_order.Id);

        var html = _sent.Single(mail => string.Equals(mail.TemplateName, "ticket_issued", StringComparison.Ordinal)).HtmlBody;
        html.IndexOf(adult.Barcode, StringComparison.Ordinal).Should().BeLessThan(html.IndexOf(older.Barcode, StringComparison.Ordinal));
        html.IndexOf(older.Barcode, StringComparison.Ordinal).Should().BeLessThan(html.IndexOf(newer.Barcode, StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task Deliver_SendsOneTicketPerHolder_AndOneReceiptToTheBuyer()
    {
        var own = Ticket(BuyerId, "Ana Buyer", "ana@example.com");
        var partner = Ticket(PartnerId, "Ben Partner", "ben@example.com");
        Tickets(own, partner, Ticket(PartnerId, "Old", "old@example.com", TicketCheckoutTicketState.Void));

        await Create().DeliverAsync(_order.Id);

        _sent.Where(m => string.Equals(m.TemplateName, "ticket_issued", StringComparison.Ordinal)).Select(m => m.RecipientEmail)
            .Should().BeEquivalentTo(["ana@example.com", "ben@example.com"]);
        _sent.Should().ContainSingle(m => string.Equals(m.TemplateName, "ticket_order_receipt", StringComparison.Ordinal) && string.Equals(m.RecipientEmail, "ana@example.com", StringComparison.Ordinal));
        _sent.Single(m => string.Equals(m.RecipientEmail, "ben@example.com", StringComparison.Ordinal)).Culture.Should().Be("de", "a holder reads their ticket in their own language");
        _sent.Single(m => string.Equals(m.RecipientEmail, "ben@example.com", StringComparison.Ordinal)).HtmlBody.Should().Contain("Ana Buyer");
        _sent.Single(m => string.Equals(m.RecipientEmail, "ben@example.com", StringComparison.Ordinal)).HtmlBody.Should().Contain(partner.Barcode);
        _jobs.ReceivedWithAnyArgs(2).Create(default!, default!);
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketDeliverySent, Arg.Any<string>(), _order.Id,
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task DeliverAsync_DonationOnly_SendsReceipt()
    {
        _order.Lines = [new TicketCheckoutOrderLine { Kind = OrderLineKind.Donation, AmountMinor = 45000 }];
        Tickets();

        await Create().DeliverAsync(_order.Id);

        var receipt = _sent.Should().ContainSingle().Subject;
        receipt.TemplateName.Should().Be("ticket_order_receipt");
        receipt.RecipientEmail.Should().Be("ana@example.com");
        receipt.DeliveryId.Should().Be(TicketDeliveryService.DeliveryIdFor("ticket_order_receipt", _order.Id));
        _jobs.DidNotReceiveWithAnyArgs().Create(default!, default!);
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketDeliverySent, Arg.Any<string>(), _order.Id,
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task Deliver_PutsAChildWithoutEmailOnTheAdultsMail_AndInvitesOnlyTheAdult()
    {
        var adult = Ticket(BuyerId, "Ana Buyer", "ana@example.com");
        var child = Ticket(BuyerId, "Kid Buyer", null, kind: TicketHolderKind.Child, parent: adult.Id);
        Tickets(adult, child);

        await Create().DeliverAsync(_order.Id);

        var ticketMail = _sent.Where(m => string.Equals(m.TemplateName, "ticket_issued", StringComparison.Ordinal)).Should().ContainSingle().Subject;
        ticketMail.HtmlBody.Should().Contain(adult.Barcode).And.Contain(child.Barcode);
        _jobs.ReceivedWithAnyArgs(1).Create(default!, default!);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task DeliverAsync_SeparateDependent_IncludesBarcode(bool resend)
    {
        var adult = Ticket(BuyerId, "Ana Buyer", "ana@example.com");
        var companion = Ticket(null, "Helper", null, kind: TicketHolderKind.Companion,
            parent: adult.Id, orderId: Guid.NewGuid());
        var retired = Ticket(null, "Retired", null, TicketCheckoutTicketState.Void,
            TicketHolderKind.Companion, adult.Id, orderId: Guid.NewGuid());
        var unrelated = Ticket(null, "Unrelated", null, kind: TicketHolderKind.Companion,
            parent: Guid.NewGuid(), orderId: Guid.NewGuid());
        Tickets(adult);
        _checkout.GetTicketsForSettingsAsync(_ticketing.Id, Arg.Any<CancellationToken>())
            .Returns([adult, companion, retired, unrelated]);

        if (resend)
            (await Create().ResendAsync(_order.Id, Guid.NewGuid())).Succeeded.Should().BeTrue();
        else
            await Create().DeliverAsync(_order.Id);

        var ticketMail = _sent.Single(m => string.Equals(m.TemplateName, "ticket_issued", StringComparison.Ordinal));
        ticketMail.RecipientEmail.Should().Be("ana@example.com");
        ticketMail.HtmlBody.Should().Contain(adult.Barcode).And.Contain(companion.Barcode)
            .And.NotContain(retired.Barcode).And.NotContain(unrelated.Barcode);
    }

    [HumansTheory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task DeliverAsync_UnaddressedCompanion_RequiresLiveParent(bool parentLive)
    {
        var adult = Ticket(BuyerId, "Ana Buyer", "ana@example.com",
            parentLive ? TicketCheckoutTicketState.Valid : TicketCheckoutTicketState.Void);
        var companion = Ticket(null, "Helper", null, kind: TicketHolderKind.Companion,
            parent: adult.Id, orderId: Guid.NewGuid());
        var order = new TicketCheckoutOrder
        {
            Id = companion.OrderId,
            TicketingSettingsId = _ticketing.Id,
            State = TicketCheckoutOrderState.Paid,
            BuyerUserId = BuyerId,
            BuyerName = "Ana Buyer",
            BuyerEmail = "ana@example.com",
            Culture = "es",
            PaidAt = _order.PaidAt,
            Lines = [new TicketCheckoutOrderLine { Kind = OrderLineKind.Ticket, HolderKind = TicketHolderKind.Companion }],
        };
        _checkout.GetOrderDetailAsync(order.Id, Arg.Any<CancellationToken>())
            .Returns(new OrderDetail(order, [], [], [companion]));
        _checkout.GetTicketsForSettingsAsync(_ticketing.Id, Arg.Any<CancellationToken>()).Returns([adult, companion]);

        await Create().DeliverAsync(order.Id);

        var ticketMails = _sent.Where(m => string.Equals(m.TemplateName, "ticket_issued", StringComparison.Ordinal)).ToList();
        if (parentLive)
        {
            var mail = ticketMails.Should().ContainSingle().Subject;
            mail.RecipientEmail.Should().Be("ana@example.com");
            mail.HtmlBody.Should().Contain(companion.Barcode).And.NotContain(adult.Barcode);
            mail.DeliveryId.Should().Be(TicketDeliveryService.DeliveryIdFor("ticket_issued", companion.Id));
        }
        else
            ticketMails.Should().BeEmpty();
        _sent.Should().ContainSingle(m => string.Equals(m.TemplateName, "ticket_order_receipt", StringComparison.Ordinal));
        _jobs.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [HumansFact]
    public async Task Deliver_IsIdempotentPerMail_ThroughStableDeliveryIds()
    {
        Tickets(Ticket(BuyerId, "Ana Buyer", "ana@example.com"));

        await Create().DeliverAsync(_order.Id);
        await Create().DeliverAsync(_order.Id);

        _sent.Should().HaveCount(4);
        _sent.Select(m => m.DeliveryId).Should().AllSatisfy(id => id.Should().NotBeNull());
        _sent.Select(m => m.DeliveryId).Distinct().Should().HaveCount(2, "the outbox dedupes the second hand-off");
    }

    [HumansFact]
    public async Task Resend_SendsFreshMail_AndAuditsTheActor()
    {
        Tickets(Ticket(BuyerId, "Ana Buyer", "ana@example.com"));
        var actor = Guid.NewGuid();

        var result = await Create().ResendAsync(_order.Id, actor);

        result.Succeeded.Should().BeTrue();
        _sent.Should().HaveCount(2).And.AllSatisfy(m => m.DeliveryId.Should().BeNull());
        _jobs.ReceivedCalls().Should().BeEmpty("a resend does not schedule a second invitation");
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketDeliveryResent, Arg.Any<string>(), _order.Id,
            Arg.Any<string>(), actor, Arg.Any<Guid?>(), Arg.Any<string?>());
        await _checkout.Received(1).RecordDeliveryAsync(_order.Id, TicketCheckoutOrderEventKind.TicketsResent,
            Arg.Any<string>(), actor, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task HandOff_OnlyEnqueues_SoHangfireRetriesTheSending()
    {
        await new TicketDeliveryHandOff(_jobs).TicketsIssuedAsync(_order.Id);

        _jobs.ReceivedWithAnyArgs(1).Create(default!, default!);
        _sent.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Deliver_FailingOnTheSecondTicket_EndsWithEveryMailQueuedOnceAfterARetry()
    {
        Tickets(Ticket(BuyerId, "Ana Buyer", "ana@example.com"), Ticket(PartnerId, "Ben Partner", "ben@example.com"));
        var calls = 0;
        _email.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (++calls == 2)
                throw new InvalidOperationException("outbox down");
            _sent.Add(call.Arg<EmailMessage>());
            return Task.CompletedTask;
        });

        var first = () => Create().DeliverAsync(_order.Id);
        await first.Should().ThrowAsync<InvalidOperationException>("Hangfire retries a job that throws");
        await Create().DeliverAsync(_order.Id);

        _sent.Select(m => m.DeliveryId).Distinct().Should().HaveCount(3, "two tickets and one receipt, each deduped by its id");
    }

    [HumansFact]
    public async Task Deliver_UsesTheOrderLanguage_ForAHolderWhoHasNeverSignedIn()
    {
        _users.GetUserInfoAsync(PartnerId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(
            new UserInfo(PartnerId, "Ben", false, "en", null, Instant.MinValue, null, null, null, null, null, null, null, null,
                "ben@example.com", [], [], null)));
        Tickets(Ticket(PartnerId, "Ben Partner", "ben@example.com"));

        await Create().DeliverAsync(_order.Id);

        _sent.Should().AllSatisfy(m => m.Culture.Should().Be("es"));
    }

    [HumansFact]
    public async Task Deliver_CountsTheMailsHandedToEmail_AndTheTicketsSkipped()
    {
        var adult = Ticket(BuyerId, "Ana Buyer", "ana@example.com");
        Tickets(adult, Ticket(BuyerId, "Kid", null, kind: TicketHolderKind.Child, parent: adult.Id),
            Ticket(Guid.NewGuid(), "No Address", null));

        await Create().DeliverAsync(_order.Id);

        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketDeliverySent, Arg.Any<string>(), _order.Id,
            "Handed 1 ticket email(s) for 2 eligible ticket(s) and the receipt to Email; 1 skipped for lack of an available recipient",
            Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<string?>());
        await _checkout.Received(1).RecordDeliveryAsync(_order.Id, TicketCheckoutOrderEventKind.TicketsEmailed,
            Arg.Any<string>(), null, Arg.Any<CancellationToken>());
        await _checkout.Received(1).RecordDeliveryAsync(_order.Id, TicketCheckoutOrderEventKind.TicketMailSkipped,
            Arg.Any<string>(), null, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Resend_IsRefused_WhenNoTicketIsLive()
    {
        Tickets(Ticket(BuyerId, "Ana Buyer", "ana@example.com", TicketCheckoutTicketState.Resold));

        var result = await Create().ResendAsync(_order.Id, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        _sent.Should().BeEmpty();
    }

    [HumansFact]
    public async Task NothingIsSentForAnUnpaidOrder()
    {
        _order.State = TicketCheckoutOrderState.Pending;
        Tickets(Ticket(BuyerId, "Ana Buyer", "ana@example.com"));

        await Create().DeliverAsync(_order.Id);
        var resend = await Create().ResendAsync(_order.Id, Guid.NewGuid());

        _sent.Should().BeEmpty();
        resend.Succeeded.Should().BeFalse();
    }

    [HumansFact]
    public async Task JoinInvitation_GoesToAValidTicketsHolder()
    {
        var ticket = Ticket(PartnerId, "Ben Partner", "ben@example.com");
        Tickets(ticket);

        await Create().SendJoinInvitationAsync(_order.Id, ticket.Id);

        var msg = _sent.Should().ContainSingle().Subject;
        msg.TemplateName.Should().Be("ticket_join_invitation");
        msg.UserId.Should().Be(PartnerId);
        msg.Category.Should().Be(MessageCategory.VolunteerUpdates, "Ticketing is locked on for ticket holders");
    }

    [HumansTheory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData(UserState.Deleted)]
    [Xunit.InlineData(UserState.Merged)]
    public async Task JoinInvitation_UnavailableHolder_SkipsStoredAddress(UserState? state)
    {
        var ticket = Ticket(PartnerId, "Ben Partner", "snapshot@example.com");
        Tickets(ticket);
        var holder = Xunit.Assert.IsType<UserInfo>(await _users.GetUserInfoAsync(PartnerId));
        _users.GetUserInfoAsync(PartnerId, Arg.Any<CancellationToken>())
            .Returns(state is { } value ? holder with { State = value } : null);

        await Create().SendJoinInvitationAsync(_order.Id, ticket.Id);

        _sent.Should().BeEmpty();
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData(null, null, false)]
    [Xunit.InlineData("", "snapshot@example.com", false)]
    [Xunit.InlineData(" \t", "snapshot@example.com", false)]
    [Xunit.InlineData(null, "snapshot@example.com", true)]
    public async Task JoinInvitation_RecipientAddress_RequiresNonblankValue(string? currentEmail, string? storedEmail, bool sent)
    {
        var ticket = Ticket(PartnerId, "Ben Partner", storedEmail);
        Tickets(ticket);
        var holder = Xunit.Assert.IsType<UserInfo>(await _users.GetUserInfoAsync(PartnerId));
        _users.GetUserInfoAsync(PartnerId, Arg.Any<CancellationToken>()).Returns(holder with { IdentityEmailColumn = currentEmail });

        await Create().SendJoinInvitationAsync(_order.Id, ticket.Id);

        _sent.Should().HaveCount(sent ? 1 : 0);
        if (sent)
            _sent.Single().RecipientEmail.Should().Be(storedEmail);
        else
            _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task JoinInvitation_MergedHolder_UsesSurvivorIdentity()
    {
        var ticket = Ticket(PartnerId, "Ben Partner", "snapshot@example.com");
        Tickets(ticket);
        var holder = Xunit.Assert.IsType<UserInfo>(await _users.GetUserInfoAsync(PartnerId));
        var survivorId = Guid.NewGuid();
        _users.GetUserInfoAsync(PartnerId, Arg.Any<CancellationToken>())
            .Returns(holder with { Id = survivorId, MergedUserIds = [PartnerId] });

        await Create().SendJoinInvitationAsync(_order.Id, ticket.Id);

        var message = _sent.Should().ContainSingle().Subject;
        message.UserId.Should().Be(survivorId);
        message.RecipientEmail.Should().Be(holder.Email);
        message.Category.Should().Be(MessageCategory.VolunteerUpdates);
        message.DeliveryId.Should().Be(TicketDeliveryService.DeliveryIdFor("ticket_join_invitation", ticket.Id));
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketJoinInvitationSent, Arg.Any<string>(), _order.Id,
            Arg.Any<string>(), Arg.Any<string>(), ticket.Id, "TicketCheckoutTicket");
    }

    [HumansTheory]
    [Xunit.InlineData(nameof(TicketCheckoutTicketState.Void))]
    [Xunit.InlineData(nameof(TicketCheckoutTicketState.Resold))]
    [Xunit.InlineData(nameof(TicketCheckoutTicketState.Offered))]
    public async Task JoinInvitation_IsSkippedOnceTheTicketIsNoLongerHeld(string state)
    {
        var ticket = Ticket(PartnerId, "Ben Partner", "ben@example.com", Enum.Parse<TicketCheckoutTicketState>(state));
        Tickets(ticket);

        await Create().SendJoinInvitationAsync(_order.Id, ticket.Id);

        _sent.Should().BeEmpty();
    }

    [HumansFact]
    public async Task PaymentEnded_TellsTheBuyerOnlyWhenTheOrderFailedOrLapsed()
    {
        await Create().SendPaymentEndedAsync(_order.Id);
        _sent.Should().BeEmpty("a paid order gets its tickets, not a failure notice");

        _order.State = TicketCheckoutOrderState.Failed;
        await Create().SendPaymentEndedAsync(_order.Id);

        var mail = _sent.Should().ContainSingle().Subject;
        mail.TemplateName.Should().Be("ticket_payment_ended");
        mail.RecipientEmail.Should().Be("ana@example.com");
        mail.DeliveryId.Should().Be(TicketDeliveryService.DeliveryIdFor("ticket_payment_ended", _order.Id));
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketHolderNotified, Arg.Any<string>(), _order.Id,
            Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task ResaleSoldAndUnsold_GoToTheHolder_OnlyWhileTheTicketIsInThatState()
    {
        var ticket = Ticket(PartnerId, "Ben Partner", "ben@example.com", TicketCheckoutTicketState.Offered);
        _checkout.GetTicketAsync(ticket.Id, Arg.Any<CancellationToken>()).Returns(ticket);

        await Create().SendResaleSoldAsync(ticket.Id, 22500);
        await Create().SendResaleUnsoldAsync(ticket.Id);
        ticket.State = TicketCheckoutTicketState.Resold;
        await Create().SendResaleSoldAsync(ticket.Id, 22500);
        await Create().SendResaleUnsoldAsync(ticket.Id);

        _sent.Select(m => m.TemplateName).Should().Equal("ticket_resale_unsold", "ticket_resale_sold");
        _sent.Should().AllSatisfy(m => m.RecipientEmail.Should().Be("ben@example.com"));
        _sent[1].Culture.Should().Be("de");
        _sent[1].HtmlBody.Should().Contain("225,00 €");
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task SendResaleNoticeAsync_OutboxFails_DoesNotComplete(bool sold)
    {
        var ticket = Ticket(PartnerId, "Ben Partner", "ben@example.com",
            sold ? TicketCheckoutTicketState.Resold : TicketCheckoutTicketState.Offered);
        var offer = new TicketResaleOffer
        {
            Id = Guid.NewGuid(),
            TicketId = ticket.Id,
            State = sold ? TicketResaleOfferState.Sold : TicketResaleOfferState.Open,
            SoldNotificationPending = sold ? true : null,
        };
        _checkout.GetTicketAsync(ticket.Id, Arg.Any<CancellationToken>()).Returns(ticket);
        _checkout.GetOffersForSettingsAsync(ticket.TicketingSettingsId, Arg.Any<CancellationToken>()).Returns([offer]);
        _email.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("Outbox unavailable"), _ => Task.CompletedTask);
        var service = Create();
        Func<Task> send = () => sold ? service.SendResaleSoldAsync(ticket.Id, 22500) : service.SendResaleUnsoldAsync(ticket.Id);

        await send.Should().ThrowAsync<InvalidOperationException>().WithMessage("Outbox unavailable");
        await _checkout.DidNotReceive().RecordDeliveryAsync(Arg.Any<Guid>(), Arg.Any<TicketCheckoutOrderEventKind>(),
            Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>());
        await send();

        await _checkout.Received(1).RecordDeliveryAsync(ticket.OrderId,
            sold ? TicketCheckoutOrderEventKind.ResaleSold : TicketCheckoutOrderEventKind.ResaleUnsold,
            Arg.Any<string>(), null, Arg.Any<CancellationToken>(), offer.Id);
        _sent.Select(m => m.DeliveryId).Distinct().Should().ContainSingle()
            .Which.Should().Be(TicketDeliveryService.DeliveryIdFor(sold ? "ticket_resale_sold" : "ticket_resale_unsold", ticket.Id));
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task SendRenamedTicketAsync_SeparateDependent_IncludesBarcode(bool separateOrder)
    {
        var adult = Ticket(PartnerId, "Ben Partner", "ben@example.com");
        var child = Ticket(null, "Kid Partner", null,
            kind: separateOrder ? TicketHolderKind.Companion : TicketHolderKind.Child, parent: adult.Id,
            orderId: separateOrder ? Guid.NewGuid() : adult.OrderId);
        Tickets(separateOrder ? [adult] : [adult, child]);
        _checkout.GetTicketsForSettingsAsync(_ticketing.Id, Arg.Any<CancellationToken>()).Returns([adult, child]);
        _checkout.GetTicketAsync(adult.Id, Arg.Any<CancellationToken>()).Returns(adult);
        var deliveryId = Guid.NewGuid();

        await Create().SendRenamedTicketAsync(adult.Id, deliveryId);

        var mail = _sent.Should().ContainSingle().Subject;
        mail.TemplateName.Should().Be("ticket_issued");
        mail.RecipientEmail.Should().Be("ben@example.com");
        mail.DeliveryId.Should().Be(deliveryId);
        mail.HtmlBody.Should().Contain(adult.Barcode).And.Contain(child.Barcode);
    }
}

using AwesomeAssertions;
using Hangfire;
using Humans.AuditLog.Contracts;
using Humans.Base.Configuration;
using Humans.Email.Contracts;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
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
        _email.SendAsync(Arg.Do<EmailMessage>(_sent.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
    }

    private TicketDeliveryService Create() => new(
        _checkout, _settings, _users, _email, TestTicketsEmails.Create(), TestTicketCodeImages.Create(), _jobs, _audit,
        Options.Create(new EmailSettings { BaseUrl = TestTicketsEmails.BaseUrl }), NullLogger<TicketDeliveryService>.Instance);

    private TicketCheckoutTicket Ticket(Guid? holderId, string name, string? email, TicketCheckoutTicketState state = TicketCheckoutTicketState.Valid,
        TicketHolderKind kind = TicketHolderKind.Adult, Guid? parent = null) => new()
        {
            Id = Guid.NewGuid(),
            ParentTicketId = parent,
            OrderId = _order.Id,
            TicketingSettingsId = _ticketing.Id,
            HolderUserId = holderId,
            HolderName = name,
            HolderEmail = email,
            Kind = kind,
            State = state,
            Barcode = "H" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant(),
        };

    private void Tickets(params TicketCheckoutTicket[] tickets) =>
        _checkout.GetOrderDetailAsync(_order.Id, Arg.Any<CancellationToken>()).Returns(_ => new OrderDetail(_order, [], [], tickets));

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
            "Handed 1 ticket email(s) for 3 live ticket(s) and the receipt to Email; 1 skipped for lack of an address",
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

    [HumansFact]
    public async Task RenamedTicket_IsSentToTheNewHolder_WithTheChildrenRidingOnIt()
    {
        var adult = Ticket(PartnerId, "Ben Partner", "ben@example.com");
        var child = Ticket(PartnerId, "Kid Partner", null, kind: TicketHolderKind.Child, parent: adult.Id);
        Tickets(adult, child);
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

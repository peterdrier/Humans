using AwesomeAssertions;
using Hangfire;
using Humans.AuditLog.Contracts;
using Humans.Base.Configuration;
using Humans.Email.Contracts;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Domain.Checkout;
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
    private readonly ITicketCheckoutRepository _repository = Substitute.For<ITicketCheckoutRepository>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IEmailService _email = Substitute.For<IEmailService>();
    private readonly IBackgroundJobClient _jobs = Substitute.For<IBackgroundJobClient>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly List<EmailMessage> _sent = [];

    private static readonly Guid BuyerId = Guid.NewGuid();
    private static readonly Guid PartnerId = Guid.NewGuid();
    private readonly TicketSalesEvent _salesEvent = new() { Id = Guid.NewGuid(), EventSettingsId = Guid.NewGuid() };
    private readonly TicketCheckoutOrder _order;

    public TicketDeliveryServiceTests()
    {
        _order = new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            SalesEventId = _salesEvent.Id,
            BuyerUserId = BuyerId,
            BuyerName = "Ana Buyer",
            BuyerEmail = "ana@example.com",
            State = TicketCheckoutOrderState.Paid,
            PaidAt = Instant.FromUtc(2027, 1, 2, 9, 0),
            TotalMinor = 45000,
            Lines =
            [
                new TicketCheckoutOrderLine { Kind = CheckoutLineKind.Ticket, AmountMinor = 22500, VatRate = 0.10m, AttendeeName = "Ana Buyer" },
                new TicketCheckoutOrderLine { Kind = CheckoutLineKind.Ticket, AmountMinor = 22500, VatRate = 0.10m, AttendeeName = "Ben Partner" },
            ],
        };
        _repository.GetOrderAsync(_order.Id, Arg.Any<CancellationToken>()).Returns(_order);
        _repository.GetSalesEventAsync(_salesEvent.Id, Arg.Any<CancellationToken>()).Returns(_salesEvent);
        _settings.GetEventSettingsByIdAsync(_salesEvent.EventSettingsId, Arg.Any<CancellationToken>())
            .Returns(new EventSettingsInfo(_salesEvent.EventSettingsId, "Nowhere 2027", 2027, "Europe/Madrid",
                new LocalDate(2027, 6, 1), 0, 5, 7, new Dictionary<int, int>(), null, null));
        _users.GetUserInfoAsync(PartnerId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(
            new UserInfo(PartnerId, "Ben", false, "de", null, Instant.MinValue, null, null, null, null, null, null, null, null,
                "ben@example.com", [], [], null)));
        _email.SendAsync(Arg.Do<EmailMessage>(_sent.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
    }

    private TicketDeliveryService Create() => new(
        _repository, _settings, _users, _email, TestTicketsEmails.Create(), TestTicketCodeImages.Create(), _jobs, _audit,
        Options.Create(new EmailSettings { BaseUrl = TestTicketsEmails.BaseUrl }), NullLogger<TicketDeliveryService>.Instance);

    private TicketCheckoutTicket Ticket(Guid holderId, string name, string email, TicketCheckoutTicketState state = TicketCheckoutTicketState.Valid) => new()
    {
        Id = Guid.NewGuid(),
        OrderId = _order.Id,
        SalesEventId = _salesEvent.Id,
        HolderUserId = holderId,
        HolderName = name,
        HolderEmail = email,
        Kind = TicketCheckoutTicketKind.Adult,
        State = state,
        Barcode = "H" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant(),
    };

    private void Tickets(params TicketCheckoutTicket[] tickets) =>
        _repository.GetTicketsForOrderAsync(_order.Id, Arg.Any<CancellationToken>()).Returns(tickets);

    [HumansFact]
    public async Task TicketsIssued_SendsOneTicketPerHolder_AndOneReceiptToThePayer()
    {
        var own = Ticket(BuyerId, "Ana Buyer", "ana@example.com");
        var partner = Ticket(PartnerId, "Ben Partner", "ben@example.com");
        Tickets(own, partner, Ticket(PartnerId, "Old", "old@example.com", TicketCheckoutTicketState.Void));

        await Create().TicketsIssuedAsync(_order.Id);

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
    public async Task TicketsIssued_IsIdempotentPerMail_ThroughStableDeliveryIds()
    {
        Tickets(Ticket(BuyerId, "Ana Buyer", "ana@example.com"));

        await Create().TicketsIssuedAsync(_order.Id);
        await Create().TicketsIssuedAsync(_order.Id);

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
    }

    [HumansFact]
    public async Task NothingIsSentForAnUnpaidOrder()
    {
        _order.State = TicketCheckoutOrderState.Pending;
        Tickets(Ticket(BuyerId, "Ana Buyer", "ana@example.com"));

        await Create().TicketsIssuedAsync(_order.Id);
        var resend = await Create().ResendAsync(_order.Id, Guid.NewGuid());

        _sent.Should().BeEmpty();
        resend.Succeeded.Should().BeFalse();
    }

    [HumansFact]
    public async Task JoinInvitation_GoesToAValidTicketsHolder()
    {
        var ticket = Ticket(PartnerId, "Ben Partner", "ben@example.com");
        _repository.GetTicketAsync(ticket.Id, Arg.Any<CancellationToken>()).Returns(ticket);

        await Create().SendJoinInvitationAsync(ticket.Id);

        var msg = _sent.Should().ContainSingle().Subject;
        msg.TemplateName.Should().Be("ticket_join_invitation");
        msg.UserId.Should().Be(PartnerId);
        msg.Category.Should().Be(MessageCategory.Ticketing);
    }

    [HumansTheory]
    [Xunit.InlineData(nameof(TicketCheckoutTicketState.Void))]
    [Xunit.InlineData(nameof(TicketCheckoutTicketState.Resold))]
    [Xunit.InlineData(nameof(TicketCheckoutTicketState.Offered))]
    public async Task JoinInvitation_IsSkippedOnceTheTicketIsNoLongerHeld(string state)
    {
        var ticket = Ticket(PartnerId, "Ben Partner", "ben@example.com", Enum.Parse<TicketCheckoutTicketState>(state));
        _repository.GetTicketAsync(ticket.Id, Arg.Any<CancellationToken>()).Returns(ticket);

        await Create().SendJoinInvitationAsync(ticket.Id);

        _sent.Should().BeEmpty();
    }
}

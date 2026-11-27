using AwesomeAssertions;
using Humans.Testing;
using Microsoft.EntityFrameworkCore;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Grants.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Controllers;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Data;
using Humans.Tickets.Services.Checkout;
using Humans.Users.Contracts;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Tickets.Tests.Services.Checkout;

public sealed partial class TicketCheckoutServiceTests
{
    private (TicketCheckoutService Service, ITicketingPlatform Other, PaymentLedger Primary, PaymentLedger Secondary,
        TicketCheckoutOrder Foreign) CreateSharedRecon()
    {
        var native = new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = _ticketing.Id,
            PlatformKey = "test",
            State = TicketCheckoutOrderState.Paid,
            TotalMinor = 10000,
            FeeMinor = 100,
            CreatedAt = Now,
        };
        var foreign = new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = _ticketing.Id,
            PlatformKey = "other",
            State = TicketCheckoutOrderState.Paid,
            TotalMinor = 20000,
            FeeMinor = 200,
            CreatedAt = Now,
        };
        _repo.Orders.AddRange([native, foreign]);
        var primary = new PaymentLedger(
            [new("txn_a", Now, PaymentLedgerEntryKind.Charge, 10000, 100, native.Id, "original a"),
                new("txn_b", Now, PaymentLedgerEntryKind.Charge, 20000, 200, null, "original b")],
            [new("po_primary", Now, Now, 29700, "paid", 29700, 2)])
        { AccountKey = "stripe:Tickets" };
        var secondary = primary with
        {
            Entries = [primary.Entries[0] with { HumansOrderId = null }, primary.Entries[1] with { HumansOrderId = foreign.Id }],
        };
        var other = Substitute.For<ITicketingPlatform>();
        other.Key.Returns("other");
        other.IsConfigured.Returns(true);
        _platform.GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, Arg.Any<CancellationToken>()).Returns(primary);
        other.GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, Arg.Any<CancellationToken>()).Returns(secondary);
        return (CreateService(_repo, [_platform, other]), other, primary, secondary, foreign);
    }

    [HumansFact]
    public async Task ReconcilePayments_SharedAccount_PreservesSnapshot()
    {
        var setup = CreateSharedRecon();
        setup.Other.GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, Arg.Any<CancellationToken>()).Returns(setup.Secondary with
        {
            Entries = [setup.Secondary.Entries[0], setup.Secondary.Entries[1] with { AmountMinor = 999, FeeMinor = 999 },
                new("txn_new", Now, PaymentLedgerEntryKind.Charge, 700, 0, setup.Foreign.Id, null)],
            Payouts = [new("po_later", Now, Now, 1, "paid", 1, 1)],
        });

        var result = (await setup.Service.ReconcilePaymentsAsync("test", Now - Duration.FromDays(1), Now, Ct))!;

        result.Balanced.Should().BeTrue();
        result.Rows.Select(r => r.Entry).Should().Equal(setup.Primary.Entries[0], setup.Primary.Entries[1] with { HumansOrderId = setup.Foreign.Id });
        result.Payouts.Select(p => p.Payout).Should().Equal(setup.Primary.Payouts);
        result.Totals.ChargedMinor.Should().Be(30000);
        result.PaymentFeesMinor.Should().Be(300);
        result.HumansPaymentFeesMinor.Should().Be(300);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcilePayments_UnmatchedAttribution_RemainsUnbalanced(bool differentAccount)
    {
        var setup = CreateSharedRecon();
        if (differentAccount)
            _platform.GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, Arg.Any<CancellationToken>()).Returns(setup.Primary with
            { Entries = [setup.Primary.Entries[0], setup.Primary.Entries[1] with { HumansOrderId = setup.Foreign.Id }] });
        setup.Other.GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, Arg.Any<CancellationToken>()).Returns(setup.Secondary with
        {
            AccountKey = differentAccount ? "stripe:Other" : setup.Secondary.AccountKey,
            Entries = [setup.Secondary.Entries[1] with { HumansOrderId = differentAccount ? setup.Foreign.Id : null }],
        });

        var result = (await setup.Service.ReconcilePaymentsAsync("test", Now - Duration.FromDays(1), Now, Ct))!;

        result.Balanced.Should().BeFalse();
        result.Problems.Should().ContainSingle().Which.Issues.Should().Equal(TicketPaymentIssue.NoOrder);
    }

    [HumansFact]
    public async Task ReconcilePayments_UntaggedAccount_RemainsScoped()
    {
        var setup = CreateSharedRecon();
        _platform.GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, Arg.Any<CancellationToken>()).Returns(setup.Secondary with { AccountKey = null });

        var result = (await setup.Service.ReconcilePaymentsAsync("test", Now - Duration.FromDays(1), Now, Ct))!;

        result.Balanced.Should().BeFalse();
        await setup.Other.DidNotReceiveWithAnyArgs().GetPaymentLedgerAsync(default, default);
    }

    [HumansFact]
    public async Task ReconcilePayments_UnusedConfiguredAdapter_AttributesOtherInstance()
    {
        var setup = CreateSharedRecon();
        _repo.Orders.Remove(setup.Foreign);
        _environment.EnvironmentName.Returns(Microsoft.Extensions.Hosting.Environments.Development);

        var result = (await setup.Service.ReconcilePaymentsAsync("test", Now - Duration.FromDays(1), Now, Ct))!;

        result.Balanced.Should().BeTrue();
        result.OtherInstance.Should().ContainSingle().Which.HumansOrderId.Should().Be(setup.Foreign.Id);
        result.Totals.ChargedMinor.Should().Be(30000);
    }

    [HumansFact]
    public async Task ReconcilePayments_NoOrderMovements_SkipsAttribution()
    {
        var setup = CreateSharedRecon();
        _platform.GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, Arg.Any<CancellationToken>()).Returns(setup.Primary with
        { Entries = [new("txn_fee", Now, PaymentLedgerEntryKind.Fee, -50, 0, null, "billing")] });
        setup.Other.GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PaymentLedger?>(new HttpRequestException("unused")));

        var result = (await setup.Service.ReconcilePaymentsAsync("test", Now - Duration.FromDays(1), Now, Ct))!;

        result.Balanced.Should().BeTrue();
        result.Totals.FeesMinor.Should().Be(50);
        await setup.Other.DidNotReceiveWithAnyArgs().GetPaymentLedgerAsync(default, default);
    }

    [HumansFact]
    public async Task ReconcilePayments_ConflictingAttribution_RefusesRead()
    {
        var setup = CreateSharedRecon();
        setup.Other.GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, Arg.Any<CancellationToken>()).Returns(setup.Secondary with
        { Entries = [setup.Secondary.Entries[0] with { HumansOrderId = setup.Foreign.Id }] });

        var result = (await setup.Service.ReconcilePaymentsAsync("test", Now - Duration.FromDays(1), Now, Ct))!;

        result.Balanced.Should().BeFalse();
        result.Error.Should().Contain("conflicting");
        result.Rows.Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcilePayments_AttributionReadFails_RefusesRead(bool malformed)
    {
        var setup = CreateSharedRecon();
        setup.Other.GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PaymentLedger?>(malformed
                ? new System.Text.Json.JsonException("attribution down") : new HttpRequestException("attribution down")));

        var result = (await setup.Service.ReconcilePaymentsAsync("test", Now - Duration.FromDays(1), Now, Ct))!;

        result.Error.Should().Contain("attribution down");
        result.Rows.Should().BeEmpty();
    }

    [HumansFact]
    public async Task ReconcilePayments_CallerCancelled_Propagates()
    {
        var setup = CreateSharedRecon();
        using var caller = new CancellationTokenSource();
        setup.Other.GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await caller.CancelAsync();
            return await Task.FromCanceled<PaymentLedger?>(caller.Token);
        });

        var read = () => setup.Service.ReconcilePaymentsAsync("test", Now - Duration.FromDays(1), Now, caller.Token);

        await read.Should().ThrowAsync<OperationCanceledException>();
        await setup.Other.Received(1).GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, caller.Token);
    }

    [HumansTheory]
    [InlineData((int)TicketPaymentIssue.FeeMismatch)]
    [InlineData((int)TicketPaymentIssue.AmountMismatch)]
    [InlineData((int)TicketPaymentIssue.NotPaidInHumans)]
    public async Task ReconcilePayments_SharedAccount_ChecksForeignOrder(int issue)
    {
        var setup = CreateSharedRecon();
        var expected = (TicketPaymentIssue)issue;
        if (expected == TicketPaymentIssue.FeeMismatch)
            setup.Foreign.FeeMinor = 201;
        else if (expected == TicketPaymentIssue.NotPaidInHumans)
            setup.Foreign.State = TicketCheckoutOrderState.Created;
        else
            _platform.GetPaymentLedgerAsync(Now - Duration.FromDays(1), Now, Arg.Any<CancellationToken>()).Returns(setup.Primary with
            { Entries = [setup.Primary.Entries[0], setup.Primary.Entries[1] with { AmountMinor = 20001 }] });

        var result = (await setup.Service.ReconcilePaymentsAsync("test", Now - Duration.FromDays(1), Now, Ct))!;

        result.Problems.Should().ContainSingle().Which.Issues.Should().Equal(expected);
    }

    [HumansFact]
    public async Task ReconcilePayments_SharedAccount_FindsLaterReturn()
    {
        var setup = CreateSharedRecon();
        setup.Foreign.State = TicketCheckoutOrderState.Failed;
        var from = Now - Duration.FromDays(30);
        var to = Now - Duration.FromDays(10);
        var payment = setup.Primary.Entries[1] with { CreatedAt = Now - Duration.FromDays(20) };
        var returned = payment with { Id = "txn_return", CreatedAt = Now - Duration.FromDays(5), AmountMinor = -20000, FeeMinor = 0 };
        _platform.GetPaymentLedgerAsync(from, to, Arg.Any<CancellationToken>()).Returns(new PaymentLedger([payment], []) { AccountKey = "stripe:Tickets" });
        setup.Other.GetPaymentLedgerAsync(from, to, Arg.Any<CancellationToken>()).Returns(new PaymentLedger([payment with { HumansOrderId = setup.Foreign.Id }], []) { AccountKey = "stripe:Tickets" });
        _platform.GetPaymentLedgerAsync(to, Now, Arg.Any<CancellationToken>()).Returns(new PaymentLedger([returned], []) { AccountKey = "stripe:Tickets" });
        setup.Other.GetPaymentLedgerAsync(to, Now, Arg.Any<CancellationToken>()).Returns(new PaymentLedger([returned with { HumansOrderId = setup.Foreign.Id }], []) { AccountKey = "stripe:Tickets" });

        var result = (await setup.Service.ReconcilePaymentsAsync("test", from, to, Ct))!;

        result.Balanced.Should().BeTrue();
        result.Totals.ChargedMinor.Should().Be(20000);
        await setup.Other.Received(1).GetPaymentLedgerAsync(to, Now, Ct);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfferForResale_ErasedIdentity_Refuses(bool emailOnly)
    {
        var ticket = await PaidAdultAsync();
        ticket.HolderName = emailOnly ? ticket.HolderName : TicketPiiTombstone.Name;
        ticket.HolderEmail = emailOnly ? TicketPiiTombstone.EmailFor(_buyerId) : ticket.HolderEmail;
        var events = _repo.Events.Count;
        _audit.ClearReceivedCalls();

        var held = (await _service.GetHeldTicketsAsync(_buyerId, Ct)).Single(t => t.Ticket.Id == ticket.Id);
        var result = await _service.OfferForResaleAsync(ticket.Id, _buyerId, Ct);

        held.CanOffer.Should().BeFalse();
        result.Refusal.Should().Be("Tickets_Resale_NotOfferable");
        ticket.State.Should().Be(TicketCheckoutTicketState.Valid);
        _repo.Offers.Should().BeEmpty();
        _repo.Events.Should().HaveCount(events);
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task InviteBuyers_ErasedIdentity_Refuses()
    {
        var ticket = await OfferedTicketAsync();
        ticket.HolderName = TicketPiiTombstone.Name;
        _email.ClearReceivedCalls();

        var held = (await _service.GetHeldTicketsAsync(_buyerId, Ct)).Single(t => t.Ticket.Id == ticket.Id);
        var result = await _service.InviteBuyersAsync(ticket.Id, _buyerId, ["bo@example.com"], new Uri("https://h.example/r/"), "en", Ct);

        held.CanInvite.Should().BeFalse();
        result.Refusal.Should().Be("Tickets_Resale_NotOfferable");
        _repo.Offers.Single().Invites.Should().BeEmpty();
        _repo.Offers.Single().DirectedInviteCount.Should().Be(0);
        _email.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartCheckout_ErasedResale_Refuses(bool directed)
    {
        var ticket = await OfferedTicketAsync();
        var offer = _repo.Offers.Single();
        offer.Invites = [new TicketResaleInvite { Email = "bo@example.com", TokenHash = TicketCheckoutService.HashToken("legacy-token") }];
        ticket.HolderEmail = TicketPiiTombstone.EmailFor(_buyerId);
        _platform.ClearReceivedCalls();
        if (directed)
            (await _service.GetDirectedBuyPageAsync("legacy-token", _otherBuyerId, Ct))!.InviteTaken.Should().BeTrue();

        var result = await _service.StartCheckoutAsync(Buy() with { DirectedToken = directed ? "legacy-token" : null }, _otherBuyerId, Ct);

        result.Refusal.Should().Be("Tickets_Buy_ResaleOnly");
        _repo.Orders.Should().ContainSingle();
        offer.State.Should().Be(TicketResaleOfferState.Open);
        offer.ReservedByOrderId.Should().BeNull();
        await _platform.DidNotReceiveWithAnyArgs().CreateOrderAsync(default!);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlatformOrderChanged_ErasedResale_IssuesFreshStock(bool released)
    {
        var old = await OfferedTicketAsync();
        _ticketing.Capacity = 1;
        var started = await _service.StartCheckoutAsync(Buy(), _otherBuyerId, Ct);
        started.Refusal.Should().BeNull();
        var buyer = _repo.Orders.Single(o => o.Id == started.OrderId);
        if (released)
        {
            _platform.GetStatusAsync(buyer.PlatformOrderId!, Arg.Any<CancellationToken>())
                .Returns(new PlatformOrderStatus(PlatformOrderState.Expired, null, null, null));
            await _service.PlatformOrderChangedAsync("test", buyer.PlatformOrderId!, Ct);
        }
        old.HolderName = TicketPiiTombstone.Name;
        var offer = _repo.Offers.Single();
        _delivery.ClearReceivedCalls();

        await PayAsync(buyer.PlatformOrderId!);
        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);
        await RefundBatchAsync();

        buyer.State.Should().Be(TicketCheckoutOrderState.Paid);
        offer.State.Should().Be(TicketResaleOfferState.Withdrawn);
        offer.ReservedByOrderId.Should().BeNull();
        offer.SoldToOrderId.Should().BeNull();
        offer.RefundId.Should().BeNull();
        offer.SoldNotificationPending.Should().BeNull();
        old.HolderName.Should().Be(TicketPiiTombstone.Name);
        (await _service.FindIssuedTicketAsync(old.Barcode, Ct))!.IsValid.Should().BeFalse();
        var issued = _repo.Tickets.Single(t => t.OrderId == buyer.Id);
        (await _service.FindIssuedTicketAsync(issued.Barcode, Ct))!.IsValid.Should().BeTrue();
        await _platform.DidNotReceive().RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
        await _delivery.DidNotReceive().ResaleSoldAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCheckoutNeedsAttention, Arg.Any<string>(), buyer.Id,
            Arg.Is<string>(text => text.Contains("erased", StringComparison.Ordinal)), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task RunResaleRefundBatch_ErasedSoldIdentity_PreservesRefund()
    {
        var old = await OfferedTicketAsync();
        var buyer = await BuyAndPayAsync(Buy(), _otherBuyerId);
        var offer = _repo.Offers.Single();
        offer.State.Should().Be(TicketResaleOfferState.Sold);
        old.HolderName = TicketPiiTombstone.Name;

        await PayAsync(buyer.PlatformOrderId!);
        var batch = await RefundBatchAsync();

        batch.Started.Should().Be(1);
        offer.State.Should().Be(TicketResaleOfferState.Sold);
        offer.RefundId.Should().Be(offer.Id);
        await _platform.Received(1).RefundAsync(Arg.Is<RefundRequest>(r => r.RefundId == offer.Id && r.AmountMinor == 22500),
            Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IssueCompanion_ErasedParent_Refuses(bool overrideCapacity)
    {
        var adult = await PaidAdultAsync();
        adult.HolderName = TicketPiiTombstone.Name;
        if (overrideCapacity)
            _ticketing.Capacity = 1;
        _provisioning.FindOrCreateUserByEmailAsync("newhelper@example.com", null, ContactSource.Manual, Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(Guid.NewGuid(), Created: true));
        var events = _repo.Events.Count;
        _delivery.ClearReceivedCalls();
        _audit.ClearReceivedCalls();

        var result = await _service.IssueCompanionAsync(adult.Id, "Helper Ruiz", "newhelper@example.com", "Support", overrideCapacity, _adminId, Ct);

        result.Succeeded.Should().BeFalse();
        _repo.Tickets.Should().ContainSingle();
        _repo.Orders.Should().ContainSingle();
        _repo.Events.Should().HaveCount(events);
        _audit.ReceivedCalls().Should().BeEmpty();
        await _provisioning.DidNotReceiveWithAnyArgs().FindOrCreateUserByEmailAsync(default!, default, default);
        await _delivery.DidNotReceiveWithAnyArgs().TicketsIssuedAsync(default);
    }

    [HumansFact]
    public async Task RequestCompanion_ErasedParent_Refuses()
    {
        var adult = await PaidAdultAsync();
        adult.HolderEmail = TicketPiiTombstone.EmailFor(_buyerId);
        Human("helper@example.com", "Helper", "Ruiz");
        var events = _repo.Events.Count;

        var held = (await _service.GetHeldTicketsAsync(_buyerId, Ct)).Single(t => t.Ticket.Id == adult.Id);
        var result = await _service.RequestCompanionAsync(adult.Id, _buyerId, "helper@example.com", "Support", Ct);

        held.CanAskCompanion.Should().BeFalse();
        result.Refusal.Should().Be("Tickets_Companion_NotAllowed");
        _repo.CompanionRequests.Should().BeEmpty();
        _repo.Events.Should().HaveCount(events);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecideCompanionRequest_ErasedParent_Refuses(bool issued)
    {
        var adult = await PaidAdultAsync();
        Human("helper@example.com", "Helper", "Ruiz");
        await _service.RequestCompanionAsync(adult.Id, _buyerId, "helper@example.com", "Support", Ct);
        var request = _repo.CompanionRequests.Single();
        if (issued)
            (await _service.IssueCompanionAsync(adult.Id, "Helper Ruiz", "helper@example.com", "Support", false, _adminId, Ct))
                .Succeeded.Should().BeTrue();
        adult.HolderName = TicketPiiTombstone.Name;
        var tickets = _repo.Tickets.Count;
        var orders = _repo.Orders.Count;
        var events = _repo.Events.Count;
        _delivery.ClearReceivedCalls();
        _audit.ClearReceivedCalls();

        var result = await _service.DecideCompanionRequestAsync(request.Id, true, null, _adminId, Ct);

        result.Succeeded.Should().BeFalse();
        request.State.Should().Be(TicketCompanionRequestState.Pending);
        request.CompanionTicketId.Should().BeNull();
        _repo.Tickets.Should().HaveCount(tickets);
        _repo.Orders.Should().HaveCount(orders);
        _repo.Events.Should().HaveCount(events);
        _audit.ReceivedCalls().Should().BeEmpty();
        await _delivery.DidNotReceiveWithAnyArgs().TicketsIssuedAsync(default);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreTicket_ErasedParent_Refuses(bool companion)
    {
        _ticketing.ChildTypes = [new TicketChildType { Name = "Child", UnderAge = 13, PriceMinor = 5000 }];
        var order = await BuyAndPayAsync(companion ? Buy() : Buy() with { Children = [new("Kid García", null)] }, _buyerId);
        var adult = _repo.Tickets.Single(t => t.OrderId == order.Id && t.Kind == TicketHolderKind.Adult);
        if (companion)
            (await _service.IssueCompanionAsync(adult.Id, "Helper Ruiz", null, "Support", false, _adminId, Ct))
                .Succeeded.Should().BeTrue();
        var dependent = _repo.Tickets.Single(t => t.ParentTicketId == adult.Id);
        (await _service.VoidTicketAsync(dependent.Id, "Voided by mistake", 0, _adminId, Ct)).Succeeded.Should().BeTrue();
        adult.HolderEmail = TicketPiiTombstone.EmailFor(_buyerId);
        var events = _repo.Events.Count;
        _repo.TicketWrites.Clear();
        _audit.ClearReceivedCalls();

        var result = await _service.RestoreTicketAsync(dependent.Id, "Restore", _adminId, Ct);

        result.Succeeded.Should().BeFalse();
        dependent.State.Should().Be(TicketCheckoutTicketState.Void);
        _repo.TicketWrites.Should().BeEmpty();
        _repo.Events.Should().HaveCount(events);
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData(UserState.Deleted)]
    [InlineData(UserState.Merged)]
    public async Task CancelEventAsync_UnavailableBuyer_CompletesWithoutEmail(UserState? state)
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        var buyer = Assert.IsType<UserInfo>(await _users.GetUserInfoAsync(_buyerId, Ct));
        _users.GetUserInfoAsync(_buyerId, Arg.Any<CancellationToken>()).Returns(state is { } value ? buyer with { State = value } : null);
        _email.ClearReceivedCalls();
        await _service.CancelEventAsync(_ticketing.Id, "Storm", _adminId, Ct);

        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);

        _email.ReceivedCalls().Should().BeEmpty();
        order.CancellationEmailedAt.Should().BeNull();
        (await _service.GetCancellationProgressAsync(_ticketing.Id, Ct))!.ToDo.Should().Be(0);
        _repo.Events.Should().ContainSingle(e => e.Kind == TicketCheckoutOrderEventKind.CancellationNoticeSkipped);
        _repo.Tickets.Should().OnlyContain(t => t.State == TicketCheckoutTicketState.Void);
        _repo.Refunds.Should().ContainSingle();
        await _platform.Received(1).RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelEventAsync_MergedBuyer_UsesCanonicalRecipient(bool retry)
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        var buyer = Assert.IsType<UserInfo>(await _users.GetUserInfoAsync(_buyerId, Ct));
        var canonical = Guid.NewGuid();
        _users.GetUserInfoAsync(_buyerId, Arg.Any<CancellationToken>())
            .Returns(buyer with { Id = canonical, IdentityEmailColumn = "survivor@example.com" });
        _email.ClearReceivedCalls();
        if (retry)
            _email.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException(new InvalidOperationException("outbox down")), Task.CompletedTask);
        await _service.CancelEventAsync(_ticketing.Id, "Storm", _adminId, Ct);

        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);

        var notices = _email.ReceivedCalls().Select(c => c.GetArguments()[0]).OfType<EmailMessage>().ToList();
        notices.Should().HaveCount(retry ? 2 : 1);
        notices.Should().OnlyContain(m => m.UserId == canonical && string.Equals(m.RecipientEmail, "survivor@example.com", StringComparison.Ordinal));
        notices.Select(m => m.DeliveryId).Distinct().Should().ContainSingle();
        order.CancellationEmailedAt.Should().NotBeNull();
        await _platform.Received(1).RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CancelEventAsync_ErasedSnapshot_RecordsSkip()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        order.BuyerName = TicketPiiTombstone.Name;
        order.BuyerEmail = TicketPiiTombstone.EmailFor(_buyerId);
        _email.ClearReceivedCalls();
        await _service.CancelEventAsync(_ticketing.Id, "Storm", _adminId, Ct);

        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);

        _email.ReceivedCalls().Should().BeEmpty();
        order.CancellationEmailedAt.Should().BeNull();
        (await _service.GetCancellationProgressAsync(_ticketing.Id, Ct))!.ToDo.Should().Be(0);
        _repo.Events.Should().ContainSingle(e => e.Kind == TicketCheckoutOrderEventKind.CancellationNoticeSkipped);
        await _platform.Received(1).RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelEventAsync_NoticeFinalized_PreservesRefreshedMoney(bool available)
    {
        var ticket = await PaidAdultAsync();
        var order = _repo.Orders.Single();
        var options = new DbContextOptionsBuilder<TicketsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var repository = new TicketCheckoutRepository(new TestDbContextFactory<TicketsDbContext>(options));
        await repository.SaveTicketingSettingsAsync(_ticketing, Ct);
        await repository.AddOrderAsync(order, Ct, ticket);
        var service = CreateService(repository);
        if (!available)
            _users.GetUserInfoAsync(_buyerId, Arg.Any<CancellationToken>()).Returns((UserInfo?)null);
        _platform.GetStatusAsync(order.PlatformOrderId!, Arg.Any<CancellationToken>())
            .Returns(new PlatformOrderStatus(PlatformOrderState.Paid, 50, "sepa_debit", Now, RefundedMinor: 22500));
        await service.CancelEventAsync(_ticketing.Id, "Storm", _adminId, Ct);

        await service.ReconcileOpenOrdersAsync(Ct);
        await service.ReconcileOpenOrdersAsync(Ct);

        var saved = (await repository.GetOrderAsync(order.Id, Ct))!;
        saved.RefundedMinor.Should().Be(22500);
        saved.FeeMinor.Should().Be(50);
        saved.PaymentMethod.Should().Be("sepa_debit");
        (saved.CancellationEmailedAt is not null).Should().Be(available);
        (await service.GetCancellationProgressAsync(_ticketing.Id, Ct))!.ToDo.Should().Be(0);
        (await repository.GetRefundsAsync(order.Id, Ct)).Should().ContainSingle();
        await _platform.Received(1).RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CancelEventAsync_RecipientLookupFails_RetriesNotice()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        var buyer = await _users.GetUserInfoAsync(_buyerId, Ct);
        _users.GetUserInfoAsync(_buyerId, Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("users down"), _ => new ValueTask<UserInfo?>(buyer));
        await _service.CancelEventAsync(_ticketing.Id, "Storm", _adminId, Ct);

        await _service.ReconcileOpenOrdersAsync(Ct);
        order.CancellationEmailedAt.Should().BeNull();
        (await _service.GetCancellationProgressAsync(_ticketing.Id, Ct))!.ToDo.Should().Be(1);
        await _service.ReconcileOpenOrdersAsync(Ct);

        order.CancellationEmailedAt.Should().NotBeNull();
        await _email.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.TemplateName == "ticket_event_cancelled"), Arg.Any<CancellationToken>());
        await _platform.Received(1).RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task RenameTicketAsync_ErasedIdentity_Refuses(bool emailOnly, bool overrideRules)
    {
        var ticket = await PaidAdultAsync();
        Human("next@example.com", "Next", "Holder");
        ticket.HolderName = emailOnly ? "Original name" : TicketPiiTombstone.Name;
        ticket.HolderEmail = emailOnly ? TicketPiiTombstone.EmailFor(_buyerId) : "old@example.com";
        var name = ticket.HolderName;
        var address = ticket.HolderEmail;
        _repo.OrderWrites.Clear();
        _repo.TicketWrites.Clear();
        _repo.Events.Clear();
        _audit.ClearReceivedCalls();
        _delivery.ClearReceivedCalls();

        var result = await _service.RenameTicketAsync(
            ticket.Id, "Next Holder", "next@example.com", "Move", overrideRules, _adminId, Ct);

        result.Succeeded.Should().BeFalse();
        ticket.HolderName.Should().Be(name);
        ticket.HolderEmail.Should().Be(address);
        ticket.State.Should().Be(TicketCheckoutTicketState.Valid);
        _repo.OrderWrites.Should().BeEmpty();
        _repo.TicketWrites.Should().BeEmpty();
        _repo.Events.Should().BeEmpty();
        _audit.ReceivedCalls().Should().BeEmpty();
        _delivery.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RefundAsync_RecoveryDeclines_ReturnsFailure(bool voidTicket, bool retry)
    {
        var ticket = await PaidAdultAsync();
        var order = _repo.Orders.Single();
        var options = new DbContextOptionsBuilder<TicketsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var repository = new TicketCheckoutRepository(new TestDbContextFactory<TicketsDbContext>(options));
        await repository.SaveTicketingSettingsAsync(_ticketing, Ct);
        await repository.AddOrderAsync(order, Ct, ticket);
        var refundId = Guid.NewGuid();
        if (retry)
        {
            ticket.VoidRefundId = refundId;
            ticket.VoidRefundMinor = 22500;
            ticket.VoidRequestedReason = "Returned";
            await repository.UpdateTicketAsync(ticket, Ct);
            await repository.AddRefundAsync(new TicketCheckoutRefund
            {
                Id = refundId,
                OrderId = order.Id,
                AmountMinor = 22500,
                Reason = "Returned",
                State = RefundState.Pending,
                CreatedAt = Now,
            }, Ct);
        }
        var service = CreateService(repository);
        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Pending, "re_1", null),
                new RefundResult(RefundState.Failed, "re_1", "Card closed"));

        var result = voidTicket
            ? await service.VoidTicketAsync(ticket.Id, "Returned", 22500, _adminId, Ct)
            : await service.RefundAsync(order.Id, 22500, "Returned", _adminId, Ct);

        result.Succeeded.Should().BeFalse();
        result.Refusal.Should().Contain("Card closed");
        var saved = (await repository.GetRefundsAsync(order.Id, Ct)).Should().ContainSingle().Which;
        saved.State.Should().Be(RefundState.Failed);
        if (retry)
            saved.Id.Should().Be(refundId);
        var live = (await repository.GetTicketAsync(ticket.Id, Ct))!;
        live.State.Should().Be(TicketCheckoutTicketState.Valid);
        live.VoidRefundId.Should().BeNull();
        (await service.FindIssuedTicketAsync(ticket.Barcode, Ct))!.IsValid.Should().BeTrue();
        await _platform.Received(2).RefundAsync(Arg.Is<RefundRequest>(r => r.RefundId == saved.Id), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(false, null)]
    [InlineData(false, UserState.Deleted)]
    [InlineData(false, UserState.Merged)]
    [InlineData(true, null)]
    [InlineData(true, UserState.Deleted)]
    [InlineData(true, UserState.Merged)]
    public async Task ResolveAccountAsync_UnavailableOwner_Refuses(bool rename, UserState? state)
    {
        var adult = await PaidAdultAsync();
        var owner = Assert.IsType<UserInfo>(await _users.GetUserInfoAsync(_otherBuyerId, Ct));
        var unavailable = state is { } value ? owner with { State = value } : null;
        _users.GetUserInfoAsync(_otherBuyerId, Arg.Any<CancellationToken>()).Returns(unavailable);
        _users.GetByEmailOrAlternateAsync("bo@example.com", Arg.Any<CancellationToken>()).Returns(unavailable);
        _userEmails.FindByAddressAsync("bo@example.com", true, true, Arg.Any<CancellationToken>())
            .Returns([Row(_otherBuyerId, verified: true)]);
        _repo.OrderWrites.Clear();
        _repo.TicketWrites.Clear();
        _repo.Events.Clear();
        _audit.ClearReceivedCalls();
        _delivery.ClearReceivedCalls();

        var result = rename
            ? await _service.RenameTicketAsync(adult.Id, "Bo Smith", "bo@example.com", "Move", false, _adminId, Ct)
            : await _service.IssueCompanionAsync(adult.Id, "Bo Smith", "bo@example.com", "Support", false, _adminId, Ct);

        result.Succeeded.Should().BeFalse();
        adult.HolderUserId.Should().Be(_buyerId);
        _repo.Orders.Should().ContainSingle();
        _repo.Tickets.Should().ContainSingle();
        _repo.OrderWrites.Should().BeEmpty();
        _repo.TicketWrites.Should().BeEmpty();
        _repo.Events.Should().BeEmpty();
        _audit.ReceivedCalls().Should().BeEmpty();
        _delivery.ReceivedCalls().Should().BeEmpty();
        _provisioning.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task RenameTicketAsync_AmbiguousVerifiedAddress_Refuses()
    {
        var adult = await PaidAdultAsync();
        var owner = await _users.GetUserInfoAsync(_otherBuyerId, Ct);
        _users.GetByEmailOrAlternateAsync("bo@example.com", Arg.Any<CancellationToken>()).Returns(owner);
        _userEmails.FindByAddressAsync("bo@example.com", true, true, Arg.Any<CancellationToken>())
            .Returns([Row(_otherBuyerId, verified: true), Row(Guid.NewGuid(), verified: true)]);
        _repo.OrderWrites.Clear();
        _repo.TicketWrites.Clear();

        var result = await _service.RenameTicketAsync(adult.Id, "Bo Smith", "bo@example.com", "Move", false, _adminId, Ct);

        result.Succeeded.Should().BeFalse();
        adult.HolderUserId.Should().Be(_buyerId);
        _repo.OrderWrites.Should().BeEmpty();
        _repo.TicketWrites.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Void_with_a_refund_refunds_first_then_voids_and_says_so()
    {
        var adult = await PaidAdultAsync();

        (await _service.VoidTicketAsync(adult.Id, "Double charged", 22500, _adminId, Ct)).Refusal.Should().BeNull();

        adult.State.Should().Be(TicketCheckoutTicketState.Void);
        await _platform.Received(1).RefundAsync(Arg.Is<RefundRequest>(r => r.AmountMinor == 22500), Arg.Any<CancellationToken>());
        _repo.Events.Should().Contain(e => e.Kind == TicketCheckoutOrderEventKind.Voided && e.Detail.Contains("refund of 225.00 requested"));
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCheckoutTicketVoided, Arg.Any<string>(), adult.OrderId,
            Arg.Is<string>(d => d.Contains("refund of 225.00 requested", StringComparison.Ordinal)), _adminId, _buyerId, AuditSubjectTypes.User);
    }

    [HumansFact]
    public async Task Void_with_no_refund_says_plainly_that_no_money_goes_back()
    {
        var adult = await PaidAdultAsync();

        (await _service.VoidTicketAsync(adult.Id, "Fraud", 0, _adminId, Ct)).Refusal.Should().BeNull();

        await _platform.DidNotReceive().RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
        _repo.Events.Should().Contain(e => e.Kind == TicketCheckoutOrderEventKind.Voided && e.Detail.Contains("no money goes back"));
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCheckoutTicketVoided, Arg.Any<string>(), adult.OrderId,
            Arg.Is<string>(d => d.Contains("with no refund", StringComparison.Ordinal)), _adminId, _buyerId, AuditSubjectTypes.User);
    }

    [HumansFact]
    public async Task Void_leaves_the_ticket_working_when_the_refund_is_turned_down_or_too_big()
    {
        var adult = await PaidAdultAsync();

        (await _service.VoidTicketAsync(adult.Id, "Too much", 22501, _adminId, Ct)).Refusal.Should().EndWith("Nothing was voided.");
        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Failed, null, "card expired"));
        (await _service.VoidTicketAsync(adult.Id, "Declined", 22500, _adminId, Ct)).Refusal
            .Should().Contain("card expired").And.EndWith("Nothing was voided.");

        adult.State.Should().Be(TicketCheckoutTicketState.Valid);
        await _audit.DidNotReceive().LogAsync(TicketsAuditActions.TicketCheckoutTicketVoided, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task Restore_brings_a_voided_ticket_back_as_an_audited_override()
    {
        var adult = await PaidAdultAsync();
        await _service.VoidTicketAsync(adult.Id, "Wrong ticket", 0, _adminId, Ct);

        (await _service.RestoreTicketAsync(adult.Id, "Voided the wrong one", _adminId, Ct)).Refusal.Should().BeNull();

        adult.State.Should().Be(TicketCheckoutTicketState.Valid);
        adult.VoidedAt.Should().BeNull();
        _repo.Events.Should().Contain(e => e.Kind == TicketCheckoutOrderEventKind.Restored && e.ActorUserId == _adminId);
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketOverride, Arg.Any<string>(), adult.OrderId,
            Arg.Is<string>(d => d.StartsWith("Override:", StringComparison.Ordinal)), _adminId, _buyerId, AuditSubjectTypes.User);
        (await _service.RestoreTicketAsync(adult.Id, "Again", _adminId, Ct)).Refusal.Should().Be("Only a voided ticket can be restored.");
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData(UserState.Deleted)]
    [InlineData(UserState.Merged)]
    public async Task RestoreTicketAsync_UnavailableHolder_Refuses(UserState? state)
    {
        var ticket = await PaidAdultAsync();
        await _service.VoidTicketAsync(ticket.Id, "Wrong ticket", 0, _adminId, Ct);
        var holder = Assert.IsType<UserInfo>(await _users.GetUserInfoAsync(_buyerId, Ct));
        _users.GetUserInfoAsync(_buyerId, Arg.Any<CancellationToken>())
            .Returns(state is { } value ? holder with { State = value } : null);
        _repo.TicketWrites.Clear();
        _repo.Events.Clear();
        _audit.ClearReceivedCalls();

        var result = await _service.RestoreTicketAsync(ticket.Id, "Restore", _adminId, Ct);

        result.Succeeded.Should().BeFalse();
        ticket.State.Should().Be(TicketCheckoutTicketState.Void);
        ticket.VoidReason.Should().Be("Wrong ticket");
        _repo.TicketWrites.Should().BeEmpty();
        _repo.Events.Should().BeEmpty();
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RestoreTicketAsync_ErasedIdentity_Refuses(bool anonymous, bool emailOnly)
    {
        var adult = await PaidAdultAsync();
        var ticket = anonymous ? AddTicket(TicketHolderKind.Child, parent: adult.Id) : adult;
        await _service.VoidTicketAsync(ticket.Id, "Erased", 0, _adminId, Ct);
        ticket.HolderName = emailOnly ? "Original name" : TicketPiiTombstone.Name;
        ticket.HolderEmail = anonymous ? null : emailOnly ? TicketPiiTombstone.EmailFor(_buyerId) : "ana@example.com";
        _repo.TicketWrites.Clear();
        _repo.Events.Clear();
        _audit.ClearReceivedCalls();

        var result = await _service.RestoreTicketAsync(ticket.Id, "Restore", _adminId, Ct);

        result.Succeeded.Should().BeFalse();
        ticket.State.Should().Be(TicketCheckoutTicketState.Void);
        _repo.TicketWrites.Should().BeEmpty();
        _repo.Events.Should().BeEmpty();
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task RestoreTicketAsync_LiveMergeSurvivor_Restores()
    {
        var ticket = await PaidAdultAsync();
        await _service.VoidTicketAsync(ticket.Id, "Wrong ticket", 0, _adminId, Ct);
        var holder = Assert.IsType<UserInfo>(await _users.GetUserInfoAsync(_buyerId, Ct));
        _users.GetUserInfoAsync(_buyerId, Arg.Any<CancellationToken>())
            .Returns(holder with { Id = Guid.NewGuid() });

        (await _service.RestoreTicketAsync(ticket.Id, "Restore", _adminId, Ct)).Succeeded.Should().BeTrue();

        ticket.State.Should().Be(TicketCheckoutTicketState.Valid);
    }

    [HumansFact]
    public async Task Restore_refuses_when_the_holder_already_holds_another_ticket()
    {
        var adult = await PaidAdultAsync();
        await _service.VoidTicketAsync(adult.Id, "Rebought", 0, _adminId, Ct);
        await BuyAndPayAsync(Buy(), _buyerId);

        (await _service.RestoreTicketAsync(adult.Id, "Oops", _adminId, Ct)).Refusal
            .Should().Be("The holder already holds another ticket for this event.");
        adult.State.Should().Be(TicketCheckoutTicketState.Void);
    }

    [HumansFact]
    public async Task Override_rename_moves_a_class_priced_ticket_and_audits_it_as_an_override()
    {
        _grants.GetActiveGrantsAsync(_buyerId, Arg.Any<CancellationToken>())
            .Returns([new ActiveGrantInfo(LowIncomeProgram, _event.Id, Now)]);
        var ticket = await PaidAdultAsync();
        var newHolder = Guid.NewGuid();
        _provisioning.FindOrCreateUserByEmailAsync("cy@example.com", null, ContactSource.Manual, Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(newHolder, Created: true));

        (await _service.RenameTicketAsync(ticket.Id, "Cy", "cy@example.com", "Board agreed", true, _adminId, Ct))
            .Refusal.Should().BeNull();

        ticket.HolderUserId.Should().Be(newHolder);
        await _audit.Received(2).LogAsync(TicketsAuditActions.TicketOverride, Arg.Any<string>(), ticket.OrderId,
            Arg.Is<string>(d => d.StartsWith("Override:", StringComparison.Ordinal)), _adminId, Arg.Any<Guid?>(), AuditSubjectTypes.User);
    }

    [HumansFact]
    public async Task Override_companion_goes_past_capacity_and_is_audited_as_an_override()
    {
        var adult = await PaidAdultAsync();
        _ticketing.Capacity = 1;

        (await _service.IssueCompanionAsync(adult.Id, "Helper Ruiz", null, "Carer", false, _adminId, Ct)).Succeeded.Should().BeFalse();
        (await _service.IssueCompanionAsync(adult.Id, "Helper Ruiz", null, "Carer", true, _adminId, Ct)).Refusal.Should().BeNull();

        _repo.Tickets.Should().ContainSingle(t => t.Kind == TicketHolderKind.Companion);
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketOverride, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), _adminId, _buyerId, AuditSubjectTypes.User);
    }

    [HumansFact]
    public async Task Team_takes_a_ticket_out_of_the_resale_queue_for_its_holder_but_not_while_a_buyer_pays()
    {
        var adult = await OfferedTicketAsync();
        var offer = _repo.Offers.Single();
        offer.State = TicketResaleOfferState.Reserved;
        (await _service.WithdrawOfferForHolderAsync(offer.Id, "Holder asked by phone", _adminId, Ct)).Succeeded.Should().BeFalse();

        offer.State = TicketResaleOfferState.Open;
        (await _service.WithdrawOfferForHolderAsync(offer.Id, "Holder asked by phone", _adminId, Ct)).Refusal.Should().BeNull();

        offer.State.Should().Be(TicketResaleOfferState.Withdrawn);
        adult.State.Should().Be(TicketCheckoutTicketState.Valid);
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketResaleWithdrawnByTeam, Arg.Any<string>(), adult.OrderId,
            Arg.Any<string>(), _adminId, _buyerId, AuditSubjectTypes.User);
    }

    [HumansTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancelEventAsync_BeforeReconciliation_StopsAdmission(bool offered, bool cancelled)
    {
        var adult = await PaidAdultAsync();
        var child = AddTicket(TicketHolderKind.Child, parent: adult.Id);
        var companion = AddTicket(TicketHolderKind.Companion, parent: adult.Id);
        if (offered)
            (await _service.OfferForResaleAsync(adult.Id, _buyerId, Ct)).Succeeded.Should().BeTrue();
        if (cancelled)
            (await _service.CancelEventAsync(_ticketing.Id, "Storm", _adminId, Ct)).Succeeded.Should().BeTrue();
        else
            _ticketing.Mode = TicketSalesMode.Closed;
        _repo.TicketWrites.Clear();

        foreach (var ticket in new[] { adult, child, companion })
        {
            var scanned = await _service.FindIssuedTicketAsync(ticket.Barcode, Ct);
            scanned.Should().NotBeNull();
            scanned!.IsValid.Should().Be(!cancelled);
            scanned.InResaleQueue.Should().BeFalse();
        }
        var listed = await _service.GetIssuedTicketsForEventAsync(_event.Id, Ct);
        listed.Should().HaveCount(3);
        listed.Should().OnlyContain(t => t.IsValid == !cancelled && !t.InResaleQueue);
        adult.State.Should().Be(offered ? TicketCheckoutTicketState.Offered : TicketCheckoutTicketState.Valid);
        child.State.Should().Be(TicketCheckoutTicketState.Valid);
        companion.State.Should().Be(TicketCheckoutTicketState.Valid);
        _repo.TicketWrites.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Cancelling_the_event_voids_refunds_and_emails_every_paid_order_once()
    {
        _ticketing.ChildTypes = [new TicketChildType { Name = "Child", UnderAge = 13, PriceMinor = 5000 }];
        var first = await BuyAndPayAsync(Buy(donation: 1000) with { Children = [new("Kid García", null)] }, _buyerId);
        var second = await BuyAndPayAsync(Buy(), _otherBuyerId);
        _email.ClearReceivedCalls();

        (await _service.CancelEventAsync(_ticketing.Id, "Storm", _adminId, Ct)).Refusal.Should().BeNull();
        _ticketing.Mode.Should().Be(TicketSalesMode.Closed);
        (await _service.CancelEventAsync(_ticketing.Id, "Again", _adminId, Ct)).Succeeded.Should().BeFalse();
        (await _service.GetCancellationProgressAsync(_ticketing.Id, Ct))!.ToDo.Should().Be(2);

        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);

        _repo.Tickets.Should().OnlyContain(t => t.State == TicketCheckoutTicketState.Void);
        await _platform.Received(1).RefundAsync(Arg.Is<RefundRequest>(r => r.PlatformOrderId == first.PlatformOrderId && r.AmountMinor == 27500),
            Arg.Any<CancellationToken>());
        await _platform.Received(1).RefundAsync(Arg.Is<RefundRequest>(r => r.PlatformOrderId == second.PlatformOrderId && r.AmountMinor == 22500),
            Arg.Any<CancellationToken>());
        await _email.Received(2).SendAsync(Arg.Is<EmailMessage>(m => m.TemplateName == "ticket_event_cancelled"), Arg.Any<CancellationToken>());
        _repo.Events.Count(e => e.Kind == TicketCheckoutOrderEventKind.EventCancelled).Should().Be(2);
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketSalesEventCancelled, Arg.Any<string>(), _ticketing.Id,
            Arg.Any<string>(), _adminId, Arg.Any<Guid?>(), Arg.Any<string?>());

        var progress = (await _service.GetCancellationProgressAsync(_ticketing.Id, Ct))!;
        progress.ToDo.Should().Be(0);
        progress.LiveTickets.Should().Be(0);
        progress.StillRefundableMinor.Should().Be(0);
        progress.DonationsMinor.Should().Be(1000);
        (progress.TicketMoneyMinor - progress.ChargedBackMinor - progress.RefundedMinor - progress.InFlightMinor - progress.StillRefundableMinor)
            .Should().Be(0);
    }

    [HumansFact]
    public async Task Cancelling_the_event_never_tells_a_resale_seller_that_there_is_no_refund()
    {
        var old = await OfferedTicketAsync();

        await _service.CancelEventAsync(_ticketing.Id, "Storm", _adminId, Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);

        await _delivery.DidNotReceiveWithAnyArgs().ResaleUnsoldAsync(default);
        _repo.Events.Should().NotContain(e => e.Kind == TicketCheckoutOrderEventKind.ResaleUnsold);
        _repo.Offers.Single().State.Should().Be(TicketResaleOfferState.Withdrawn);
        _repo.Tickets.Single(t => t.Id == old.Id).State.Should().Be(TicketCheckoutTicketState.Void);
    }

    [HumansFact]
    public async Task The_evening_resale_batch_skips_a_cancelled_event_whose_cancellation_refunded_both_orders()
    {
        var old = await OfferedTicketAsync();
        var buyer = await BuyAndPayAsync(Buy(), _otherBuyerId);
        await _service.CancelEventAsync(_ticketing.Id, "Storm", _adminId, Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);
        _platform.ClearReceivedCalls();
        _audit.ClearReceivedCalls();

        (await RefundBatchAsync()).Should().Be(new ResaleRefundBatch(0, 0, 0, 0));

        await _platform.DidNotReceive().RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
        _repo.Offers.Single().RefundId.Should().BeNull("nothing is held back: the cancellation already refunded the seller");
        _repo.Refunds.Select(r => r.Refund).Where(r => r.OrderId == old.OrderId || r.OrderId == buyer.Id)
            .Should().HaveCount(2).And.OnlyContain(r => r.AmountMinor == 22500 && r.State != RefundState.Failed);
        await _audit.DidNotReceive().LogAsync(TicketsAuditActions.TicketCheckoutNeedsAttention, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task A_refund_the_provider_turns_down_after_cancelling_is_not_tried_again_and_shows_as_still_to_refund()
    {
        await BuyAndPayAsync(Buy(), _buyerId);
        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Failed, null, "card closed"));
        await _service.CancelEventAsync(_ticketing.Id, "Storm", _adminId, Ct);

        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);

        await _platform.Received(1).RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
        var progress = (await _service.GetCancellationProgressAsync(_ticketing.Id, Ct))!;
        progress.ToDo.Should().Be(0);
        progress.StillRefundableMinor.Should().Be(22500);
        progress.RefundsSince.Should().ContainSingle(r => r.State == RefundState.Failed && r.AmountMinor == 22500);
        _repo.Events.Should().ContainSingle(e => e.Kind == TicketCheckoutOrderEventKind.EventCancelled && e.Detail.Contains("by hand"));
        await _email.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.TemplateName == "ticket_event_cancelled"
            && m.HtmlBody.Contains("Tickets_Cancel_Email_RefundByHand")), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(TicketsAuditActions.TicketCheckoutNeedsAttention, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Is<string>(d => d.Contains("card closed")), Arg.Any<string>());
    }

    [HumansFact]
    public async Task CancelEventAsync_RefundFailsDuringRefresh_UsesManualNotice()
    {
        await BuyAndPayAsync(Buy(), _buyerId);
        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.Pending, "re_1", null),
                new RefundResult(RefundState.Failed, "re_1", "Card closed"));
        _email.ClearReceivedCalls();
        await _service.CancelEventAsync(_ticketing.Id, "Storm", _adminId, Ct);

        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);

        _repo.Refunds.Should().ContainSingle().Which.Refund.State.Should().Be(RefundState.Failed);
        await _email.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.TemplateName == "ticket_event_cancelled"
            && m.HtmlBody.Contains("Tickets_Cancel_Email_RefundByHand")), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelEventAsync_ManualRefund_UsesManualNotice(bool retry)
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        var manualUrl = new Uri("https://payments.example/refund");
        _platform.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(RefundState.ManualRequired, null, "Bank transfer", manualUrl));
        _email.ClearReceivedCalls();
        if (retry)
            _email.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException(new InvalidOperationException("outbox down")), Task.CompletedTask);
        await _service.CancelEventAsync(_ticketing.Id, "Storm", _adminId, Ct);

        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);

        var notices = _email.ReceivedCalls().Select(c => c.GetArguments()[0]).OfType<EmailMessage>()
            .Where(m => string.Equals(m.TemplateName, "ticket_event_cancelled", StringComparison.Ordinal)).ToList();
        notices.Should().HaveCount(retry ? 2 : 1);
        notices.Should().OnlyContain(m => m.HtmlBody.Contains("Tickets_Cancel_Email_RefundByHand"));
        notices.Select(m => m.DeliveryId).Distinct().Should().ContainSingle();
        _repo.Refunds.Should().ContainSingle().Which.Refund.State.Should().Be(RefundState.ManualRequired);
        _repo.Refunds.Single().Refund.ManualRefundUrl.Should().Be(manualUrl.ToString());
        await _platform.Received(1).RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
        order.CancellationEmailedAt.Should().NotBeNull();
        var progress = (await _service.GetCancellationProgressAsync(_ticketing.Id, Ct))!;
        progress.ToDo.Should().Be(0);
        progress.InFlightMinor.Should().Be(22500);
        _repo.Events.Where(e => e.Kind == TicketCheckoutOrderEventKind.EventCancelled)
            .Should().OnlyContain(e => e.Detail.Contains("by hand"));
    }

    [HumansFact]
    public async Task A_cancellation_email_that_fails_is_sent_next_run_naming_the_refund_already_asked_for()
    {
        await BuyAndPayAsync(Buy(), _buyerId);
        _email.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("outbox down")), Task.CompletedTask);
        await _service.CancelEventAsync(_ticketing.Id, "Storm", _adminId, Ct);

        await _service.ReconcileOpenOrdersAsync(Ct);
        (await _service.GetCancellationProgressAsync(_ticketing.Id, Ct))!.ToDo.Should().Be(1);
        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);

        await _platform.Received(1).RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
        var sent = _email.ReceivedCalls().Select(c => c.GetArguments()[0]).OfType<EmailMessage>()
            .Where(m => string.Equals(m.TemplateName, "ticket_event_cancelled", StringComparison.Ordinal)).ToList();
        sent.Should().HaveCount(2);
        sent.Should().OnlyContain(m => m.HtmlBody.Contains("Tickets_Cancel_Email_Refund") && m.DeliveryId == sent[0].DeliveryId);
        (await _service.GetCancellationProgressAsync(_ticketing.Id, Ct))!.ToDo.Should().Be(0);
    }

    [HumansFact]
    public async Task A_donation_only_order_gets_the_cancellation_email_and_no_refund()
    {
        await BuyAndPayAsync(Buy(forSelf: false, donation: 5000), _buyerId);
        _email.ClearReceivedCalls();
        await _service.CancelEventAsync(_ticketing.Id, "Storm", _adminId, Ct);

        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);

        await _platform.DidNotReceive().RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
        await _email.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.TemplateName == "ticket_event_cancelled"
            && m.HtmlBody.Contains("Tickets_Cancel_Email_Donation") && !m.HtmlBody.Contains("Tickets_Cancel_Email_Refund")),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task A_cancelled_event_refuses_ticketing_settings_changes_resale_and_restores()
    {
        var adult = await PaidAdultAsync();
        await _service.CancelEventAsync(_ticketing.Id, "Storm", _adminId, Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);

        (await _service.SetResaleOpenAsync(_ticketing.Id, true, _adminId, Ct)).Succeeded.Should().BeFalse();
        (await _service.RestoreTicketAsync(adult.Id, "Oops", _adminId, Ct)).Refusal.Should().Be("The event is cancelled; its tickets stay void.");
    }

    [HumansFact]
    public async Task Cancelling_waits_for_a_buyer_paying_for_a_ticket_in_resale()
    {
        var adult = await OfferedTicketAsync();
        _repo.Offers.Single().State = TicketResaleOfferState.Reserved;
        await _service.CancelEventAsync(_ticketing.Id, "Storm", _adminId, Ct);

        await _service.ReconcileOpenOrdersAsync(Ct);

        adult.State.Should().Be(TicketCheckoutTicketState.Offered);
        (await _service.GetCancellationProgressAsync(_ticketing.Id, Ct))!.ToDo.Should().Be(1);
    }

    [HumansFact]
    public async Task A_team_refund_after_cancelling_does_not_stand_in_for_the_rest_of_the_cancellation_refund()
    {
        _ticketing.ChildTypes = [new TicketChildType { Name = "Child", UnderAge = 13, PriceMinor = 5000 }];
        var order = await BuyAndPayAsync(Buy() with { Children = [new("Kid García", null)] }, _buyerId);
        var child = _repo.Tickets.Single(t => t.Kind == TicketHolderKind.Child);
        await _service.CancelEventAsync(_ticketing.Id, "Storm", _adminId, Ct);
        (await _service.VoidTicketAsync(child.Id, "Asked first", 5000, _adminId, Ct)).Refusal.Should().BeNull();

        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);

        await _platform.Received(1).RefundAsync(Arg.Is<RefundRequest>(r => r.PlatformOrderId == order.PlatformOrderId && r.AmountMinor == 22500),
            Arg.Any<CancellationToken>());
        var progress = (await _service.GetCancellationProgressAsync(_ticketing.Id, Ct))!;
        progress.ToDo.Should().Be(0);
        progress.StillRefundableMinor.Should().Be(0);
    }

    [HumansFact]
    public async Task A_cancellation_refund_that_cannot_start_is_written_as_failed_and_emailed_once()
    {
        var order = await BuyAndPayAsync(Buy(), _buyerId);
        order.PlatformOrderId = null;
        _email.ClearReceivedCalls();
        await _service.CancelEventAsync(_ticketing.Id, "Storm", _adminId, Ct);

        await _service.ReconcileOpenOrdersAsync(Ct);
        await _service.ReconcileOpenOrdersAsync(Ct);

        _repo.Refunds.Should().ContainSingle(r => r.Refund.OrderId == order.Id && r.Refund.State == RefundState.Failed);
        await _email.Received(1).SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        _repo.Events.Should().ContainSingle(e => e.Kind == TicketCheckoutOrderEventKind.EventCancelled && e.Detail.Contains("by hand"));
        (await _service.GetCancellationProgressAsync(_ticketing.Id, Ct))!.ToDo.Should().Be(0);
    }

    [HumansFact]
    public async Task A_void_suggests_the_ticket_and_its_children_on_the_order_within_what_can_be_refunded()
    {
        _ticketing.ChildTypes = [new TicketChildType { Name = "Child", UnderAge = 13, PriceMinor = 5000 }];
        var order = await BuyAndPayAsync(Buy() with { Children = [new("Kid García", null)] }, _buyerId);
        var adult = _repo.Tickets.Single(t => t.Kind == TicketHolderKind.Adult);
        var child = _repo.Tickets.Single(t => t.Kind == TicketHolderKind.Child);

        var detail = (await _service.GetOrderDetailAsync(order.Id, Ct))!;
        detail.VoidRefundMinor(adult).Should().Be(27500);
        detail.VoidRefundMinor(child).Should().Be(5000);

        await _service.RefundAsync(order.Id, 25000, "Partly back", _adminId, Ct);
        (await _service.GetOrderDetailAsync(order.Id, Ct))!.VoidRefundMinor(adult).Should().Be(2500);
    }

    [HumansTheory]
    [InlineData("Pending", -1, false, false)]
    [InlineData("Pending", 1, true, false)]
    [InlineData("Processing", 1, false, false)]
    [InlineData("Expired", -1, true, false)]
    [InlineData("Pending", 1, false, true)]
    [InlineData("Created", 1, false, true)]
    public void A_reserved_offer_is_stuck_once_its_order_no_longer_holds_it(string state, int hoursPastHold, bool stuck, bool remote)
    {
        var holding = new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            State = Enum.Parse<TicketCheckoutOrderState>(state),
            ExpiresAt = Now - Duration.FromHours(hoursPastHold),
            PlatformOrderId = remote ? "remote" : null,
        };
        var offer = new TicketResaleOffer { Id = Guid.NewGuid(), State = TicketResaleOfferState.Reserved, ReservedByOrderId = holding.Id };
        var model = new ResaleViewModel(_ticketing.Id, "Event", _ticketing, [],
            new Dictionary<Guid, TicketCheckoutOrder> { [holding.Id] = holding }, Now);

        model.IsStuck(offer).Should().Be(stuck);
    }
}

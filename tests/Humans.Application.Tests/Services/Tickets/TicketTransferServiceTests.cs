using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Tickets;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Humans.Application.Tests.Services.Tickets;

/// <summary>
/// Pure unit tests for the manual-transfer <see cref="TicketTransferService"/>:
/// no vendor calls; approve = "mark successful", reject = "cancel with reason";
/// request emails sender + team, decisions email sender + receiver. All deps are
/// NSubstitute substitutes.
/// </summary>
public sealed class TicketTransferServiceTests
{
    private static readonly Instant _now = Instant.FromUtc(2026, 5, 5, 10, 0);
    private readonly FakeClock _clock = new(_now);

    private static readonly Guid _senderId = Guid.NewGuid();
    private static readonly Guid _receiverId = Guid.NewGuid();
    private static readonly Guid _adminId = Guid.NewGuid();
    private static readonly Guid _attendeeId = Guid.NewGuid();
    private static readonly Guid _orderId = Guid.NewGuid();

    private readonly ITicketTransferRepository _transferRepo = Substitute.For<ITicketTransferRepository>();
    private readonly ITicketRepository _ticketRepo = Substitute.For<ITicketRepository>();
    private readonly ITicketVendorService _vendor = Substitute.For<ITicketVendorService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IEmailMessageFactory _emailMessages = Substitute.For<IEmailMessageFactory>();
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();
    private readonly ITicketCacheInvalidator _cacheInvalidator = Substitute.For<ITicketCacheInvalidator>();

    private readonly TicketTransferService _service;

    public TicketTransferServiceTests()
    {
        _service = CreateService();

        // Receiver: complete profile + primary email.
        _userService.GetUserInfoAsync(_receiverId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(
                MakeUser(_receiverId, "Alice"),
                new Profile { BurnerName = "Alice", FirstName = "Alice", LastName = "Smith" }));
        _userEmailService.GetPrimaryEmailAsync(_receiverId, Arg.Any<CancellationToken>())
            .Returns("alice@example.com");

        // Sender: display name + primary email (for emails).
        _userService.GetUserInfoAsync(_senderId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(
                MakeUser(_senderId, "Bob"),
                new Profile { BurnerName = "Bob", FirstName = "Bob", LastName = "Jones" }));
        _userEmailService.GetPrimaryEmailAsync(_senderId, Arg.Any<CancellationToken>())
            .Returns("bob@example.com");

        _transferRepo.GetBySenderAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);

        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ids = callInfo.Arg<IReadOnlyCollection<Guid>>();
                IReadOnlyDictionary<Guid, UserInfo> dict = ids.ToDictionary(
                    id => id,
                    id => MakeUser(id, id.ToString()).ToUserInfo());
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(dict);
            });
    }

    // ── GetConfirmationAsync ────────────────────────────────────────────────────

    [HumansFact]
    public async Task GetConfirmation_ReturnsSummary_ForValidPair()
    {
        StubAttendee(TicketAttendeeStatus.Valid, _senderId);

        var confirm = await _service.GetConfirmationAsync(_attendeeId, _receiverId, _senderId, Xunit.TestContext.Current.CancellationToken);

        confirm.Should().NotBeNull();
        confirm.ReceiverLegalName.Should().Be("Alice Smith");
        confirm.ReceiverEmail.Should().Be("alice@example.com");
        confirm.VendorTicketId.Should().Be("tkt_original");
    }

    [HumansFact]
    public async Task GetConfirmation_Null_WhenReceiverIsSender()
    {
        StubAttendee(TicketAttendeeStatus.Valid, _senderId);
        var confirm = await _service.GetConfirmationAsync(_attendeeId, _senderId, _senderId, Xunit.TestContext.Current.CancellationToken);
        confirm.Should().BeNull();
    }

    [HumansFact]
    public async Task GetConfirmation_Null_WhenNotOwner()
    {
        StubAttendee(TicketAttendeeStatus.Valid, Guid.NewGuid());
        var confirm = await _service.GetConfirmationAsync(_attendeeId, _receiverId, _senderId, Xunit.TestContext.Current.CancellationToken);
        confirm.Should().BeNull();
    }

    [HumansFact]
    public async Task GetConfirmation_Null_WhenNotValid()
    {
        StubAttendee(TicketAttendeeStatus.Void, _senderId);
        var confirm = await _service.GetConfirmationAsync(_attendeeId, _receiverId, _senderId, Xunit.TestContext.Current.CancellationToken);
        confirm.Should().BeNull();
    }

    [HumansFact]
    public async Task GetConfirmation_Null_WhenCheckedIn()
    {
        // A gate scan keeps Status = Valid but records CheckedInAt — a used ticket
        // must not be transferable. nobodies-collective/Humans#736.
        StubAttendee(TicketAttendeeStatus.Valid, _senderId, checkedInAt: _now);
        var confirm = await _service.GetConfirmationAsync(_attendeeId, _receiverId, _senderId, Xunit.TestContext.Current.CancellationToken);
        confirm.Should().BeNull();
    }

    // ── CreateRequestAsync ──────────────────────────────────────────────────────

    [HumansFact]
    public async Task CreateRequest_Persists_Audits_AndEmailsSenderAndTeam()
    {
        StubAttendee(TicketAttendeeStatus.Valid, _senderId);

        await _service.CreateRequestAsync(
            new TicketTransferRequestDto(_attendeeId, _receiverId, "Going abroad"), _senderId, Xunit.TestContext.Current.CancellationToken);

        await _transferRepo.Received(1).AddAsync(
            Arg.Is<TicketTransferRequest>(r =>
                r.Status == TicketTransferStatus.Pending
                && r.ReceiverLegalName == "Alice Smith"
                && r.ReceiverEmail == "alice@example.com"),
            Arg.Any<CancellationToken>());
        await _auditLog.Received(1).LogAsync(
            AuditAction.TicketTransferRequested, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), _senderId, _receiverId, Arg.Any<string>());
        _emailMessages.Received(1).TicketTransferRequested(
            "bob@example.com", Arg.Any<string>(), "Alice Smith", Arg.Any<string>(),
            Arg.Any<string?>());
        _emailMessages.Received(1).TicketTransferTeamNotification(
            Arg.Any<string>(), "Alice Smith", "alice@example.com", Arg.Any<string>(),
            "Going abroad", Arg.Any<string>());
    }

    [HumansFact]
    public async Task CreateRequest_StillNotifiesTeam_WhenSenderEmailFails()
    {
        StubAttendee(TicketAttendeeStatus.Valid, _senderId);
        _emailMessages.TicketTransferRequested(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>())
            .Returns(_ => throw new InvalidOperationException("smtp down"));

        // Must not throw (request is already persisted) and the team must still be alerted.
        await _service.CreateRequestAsync(
            new TicketTransferRequestDto(_attendeeId, _receiverId, "x"), _senderId, Xunit.TestContext.Current.CancellationToken);

        _emailMessages.Received(1).TicketTransferTeamNotification(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task CreateRequest_Throws_WhenReceiverIsSender()
    {
        var act = () => _service.CreateRequestAsync(
            new TicketTransferRequestDto(_attendeeId, _senderId, "x"), _senderId, Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task CreateRequest_Throws_WhenNotOwner()
    {
        StubAttendee(TicketAttendeeStatus.Valid, Guid.NewGuid());
        var act = () => _service.CreateRequestAsync(
            new TicketTransferRequestDto(_attendeeId, _receiverId, "x"), _senderId, Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task CreateRequest_Throws_WhenNotValid()
    {
        StubAttendee(TicketAttendeeStatus.Void, _senderId);
        var act = () => _service.CreateRequestAsync(
            new TicketTransferRequestDto(_attendeeId, _receiverId, "x"), _senderId, Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task CreateRequest_Throws_WhenCheckedIn()
    {
        // Status stays Valid after a gate scan (nobodies-collective/Humans#736);
        // the CheckedInAt guard must still block the transfer of a used ticket.
        StubAttendee(TicketAttendeeStatus.Valid, _senderId, checkedInAt: _now);
        var act = () => _service.CreateRequestAsync(
            new TicketTransferRequestDto(_attendeeId, _receiverId, "x"), _senderId, Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Checked-in tickets cannot be transferred.");
    }

    [HumansFact]
    public async Task CreateRequest_Throws_WhenDuplicatePending()
    {
        StubAttendee(TicketAttendeeStatus.Valid, _senderId);
        _transferRepo.GetBySenderAsync(_senderId, Arg.Any<CancellationToken>())
            .Returns(new[] { MakePending(Guid.NewGuid()) });

        var act = () => _service.CreateRequestAsync(
            new TicketTransferRequestDto(_attendeeId, _receiverId, "x"), _senderId, Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── CancelAsync ─────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Cancel_SetsCancelled_ForSender()
    {
        var req = MakePending(Guid.NewGuid());
        _transferRepo.GetByIdAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);

        await _service.CancelAsync(req.Id, _senderId, Xunit.TestContext.Current.CancellationToken);

        req.Status.Should().Be(TicketTransferStatus.Cancelled);
        await _auditLog.Received(1).LogAsync(
            AuditAction.TicketTransferCancelled, Arg.Any<string>(), req.Id, Arg.Any<string>(), _senderId);
    }

    [HumansFact]
    public async Task Cancel_Throws_WhenNotSender()
    {
        var req = MakePending(Guid.NewGuid());
        _transferRepo.GetByIdAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);

        var act = () => _service.CancelAsync(req.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task Cancel_Throws_WhenVoidAlreadyCommitted()
    {
        var req = MakePending(Guid.NewGuid());
        req.VendorResult = TicketTransferVendorResult.VoidSucceededIssueFailed;
        _transferRepo.GetByIdAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);

        // The original ticket is already voided — the Sender must not cancel it away.
        var act = () => _service.CancelAsync(req.Id, _senderId, Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();
        req.Status.Should().Be(TicketTransferStatus.Pending);
    }

    // ── ApproveAsync (mark successful) ──────────────────────────────────────────

    [HumansFact]
    public async Task Approve_MarksApproved_NoVendor_EmailsBothParties()
    {
        var req = MakePending(Guid.NewGuid());
        _transferRepo.GetByIdAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);
        StubAttendee(TicketAttendeeStatus.Valid, _senderId);

        await _service.ApproveAsync(req.Id, _adminId, "looks good", Xunit.TestContext.Current.CancellationToken);

        req.Status.Should().Be(TicketTransferStatus.Approved);
        req.DecidedByUserId.Should().Be(_adminId);
        await _auditLog.Received(1).LogAsync(
            AuditAction.TicketTransferApproved, Arg.Any<string>(), req.Id, Arg.Any<string>(),
            _adminId, _senderId, Arg.Any<string>());
        // Sender + Receiver each get a "successful" decision email.
        _emailMessages.Received(2).TicketTransferDecision(
            Arg.Any<string>(), Arg.Any<string>(), true, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ApproveAsync_EvictsOrderProjectionCache()
    {
        var req = MakePending(Guid.NewGuid());
        _transferRepo.GetByIdAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);
        StubAttendee(TicketAttendeeStatus.Valid, _senderId);

        await _service.ApproveAsync(req.Id, _adminId, null, Xunit.TestContext.Current.CancellationToken);

        _cacheInvalidator.Received(1).InvalidateAfterTransfer(_senderId, _receiverId);
    }

    // ── ProcessTransferAsync (automated void+reissue) ───────────────────────────

    [HumansFact]
    public async Task Process_VoidToHoldThenReissue_KeepsOriginalPrice_MarksApproved()
    {
        var svc = CreateService();
        var req = MakePending(Guid.NewGuid());
        _transferRepo.GetByIdAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);
        StubAttendee(TicketAttendeeStatus.Valid, _senderId);

        _vendor.VoidIssuedTicketAsync("tkt_original", true, Arg.Any<CancellationToken>())
            .Returns(new VoidIssuedTicketResult("tkt_original", "hold_123"));
        _vendor.IssueTicketAsync(Arg.Any<IssueTicketRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VendorTicketDto("tt_new", null, "Alice Smith", "alice@example.com", "Full Week", 0m, "valid"));

        await svc.ProcessTransferAsync(req.Id, _adminId, "go", Xunit.TestContext.Current.CancellationToken);

        // Reissue went through the hold (same ticket class), never fresh inventory — and uses a
        // detached token (CancellationToken.None) so an aborted request can't strand the void.
        await _vendor.Received(1).IssueTicketAsync(
            Arg.Is<IssueTicketRequest>(r => r.HoldId == "hold_123" && r.EventId == null
                && r.FullName == "Alice Smith" && r.Email == "alice@example.com"),
            CancellationToken.None);

        // Old row Void + new Valid row written together (also detached); new row keeps the ORIGINAL €200 price.
        await _ticketRepo.Received(1).UpsertAttendeesAsync(
            Arg.Is<IReadOnlyList<TicketAttendee>>(list =>
                list.Any(a => a.VendorTicketId == "tt_new" && a.Status == TicketAttendeeStatus.Valid
                    && a.Price == 200m && a.MatchedUserId == _receiverId)
                && list.Any(a => a.VendorTicketId == "tkt_original" && a.Status == TicketAttendeeStatus.Void)),
            CancellationToken.None);

        req.Status.Should().Be(TicketTransferStatus.Approved);
        req.VendorResult.Should().Be(TicketTransferVendorResult.Succeeded);
        req.NewVendorTicketId.Should().Be("tt_new");
        _emailMessages.Received(2).TicketTransferDecision(
            Arg.Any<string>(), Arg.Any<string>(), true, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task Process_VoidFails_StaysPending_NoLocalWrite_NoEmails()
    {
        var svc = CreateService();
        var req = MakePending(Guid.NewGuid());
        _transferRepo.GetByIdAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);
        StubAttendee(TicketAttendeeStatus.Valid, _senderId);

        _vendor.VoidIssuedTicketAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TicketVendorWriteException("sold out", TicketVendorFailureKind.Validation));

        var act = () => svc.ProcessTransferAsync(req.Id, _adminId, null, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        req.Status.Should().Be(TicketTransferStatus.Pending);
        req.VendorResult.Should().Be(TicketTransferVendorResult.Failed);
        // Audited under the dedicated auto-failed action, NOT TicketTransferApproved.
        await _auditLog.Received(1).LogAsync(
            AuditAction.TicketTransferAutoFailed, Arg.Any<string>(), req.Id, Arg.Any<string>(),
            _adminId, _senderId, Arg.Any<string>());
        await _ticketRepo.DidNotReceive().UpsertAttendeesAsync(
            Arg.Any<IReadOnlyList<TicketAttendee>>(), Arg.Any<CancellationToken>());
        _emailMessages.DidNotReceive().TicketTransferDecision(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task Process_VoidOkButIssueFails_VoidsOriginalLocally_StaysPending()
    {
        var svc = CreateService();
        var req = MakePending(Guid.NewGuid());
        _transferRepo.GetByIdAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);
        StubAttendee(TicketAttendeeStatus.Valid, _senderId);

        _vendor.VoidIssuedTicketAsync("tkt_original", true, Arg.Any<CancellationToken>())
            .Returns(new VoidIssuedTicketResult("tkt_original", "hold_123"));
        _vendor.IssueTicketAsync(Arg.Any<IssueTicketRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TicketVendorWriteException("boom", TicketVendorFailureKind.Transient));

        var act = () => svc.ProcessTransferAsync(req.Id, _adminId, null, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        req.Status.Should().Be(TicketTransferStatus.Pending);
        req.VendorResult.Should().Be(TicketTransferVendorResult.VoidSucceededIssueFailed);
        req.VendorMessage.Should().Contain("hold_123");
        req.VendorHoldId.Should().Be("hold_123"); // captured for one-click retry
        // Original mirrored to Void locally (seat already gone at the vendor); hold retained.
        await _ticketRepo.Received(1).UpsertAttendeesAsync(
            Arg.Is<IReadOnlyList<TicketAttendee>>(list =>
                list.Count == 1 && list[0].VendorTicketId == "tkt_original"
                && list[0].Status == TicketAttendeeStatus.Void),
            Arg.Any<CancellationToken>());
        _emailMessages.DidNotReceive().TicketTransferDecision(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task Process_VoidOk_NonVendorIssueException_TreatedAsPartial()
    {
        // A non-TicketVendorWriteException after the void commits (e.g. HttpClient timeout)
        // must NOT escape — the void already happened, so it has to land in the partial path.
        var svc = CreateService();
        var req = MakePending(Guid.NewGuid());
        _transferRepo.GetByIdAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);
        StubAttendee(TicketAttendeeStatus.Valid, _senderId);

        _vendor.VoidIssuedTicketAsync("tkt_original", true, Arg.Any<CancellationToken>())
            .Returns(new VoidIssuedTicketResult("tkt_original", "hold_123"));
        _vendor.IssueTicketAsync(Arg.Any<IssueTicketRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("timeout"));

        var act = () => svc.ProcessTransferAsync(req.Id, _adminId, null, Xunit.TestContext.Current.CancellationToken);

        // Surfaces as the actionable InvalidOperationException, not the raw TaskCanceledException.
        await act.Should().ThrowAsync<InvalidOperationException>();
        req.VendorResult.Should().Be(TicketTransferVendorResult.VoidSucceededIssueFailed);
        req.VendorMessage.Should().Contain("hold_123");
        req.VendorHoldId.Should().Be("hold_123");
        await _ticketRepo.Received(1).UpsertAttendeesAsync(
            Arg.Is<IReadOnlyList<TicketAttendee>>(list =>
                list.Count == 1 && list[0].VendorTicketId == "tkt_original"
                && list[0].Status == TicketAttendeeStatus.Void),
            Arg.Any<CancellationToken>());
    }

    // ── RetryReissueAsync (one-click recovery from a partial void) ──────────────

    [HumansFact]
    public async Task RetryReissue_FromHold_IssuesNewTicket_MarksApproved()
    {
        var req = MakePending(Guid.NewGuid());
        req.VendorResult = TicketTransferVendorResult.VoidSucceededIssueFailed;
        req.VendorHoldId = "hold_123";
        _transferRepo.GetByIdAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);
        // Original attendee was already voided locally during the partial failure.
        _ticketRepo.GetAttendeeByIdAsync(_attendeeId, Arg.Any<CancellationToken>())
            .Returns(MakeAttendee(_attendeeId, _orderId, _senderId, TicketAttendeeStatus.Void));
        _vendor.IssueTicketAsync(Arg.Any<IssueTicketRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VendorTicketDto("tt_retry", null, "Alice Smith", "alice@example.com", "Full Week", 0m, "valid"));

        await _service.RetryReissueAsync(req.Id, _adminId, "go", Xunit.TestContext.Current.CancellationToken);

        // Reissued from the recorded hold — same ticket type, no second void.
        await _vendor.Received(1).IssueTicketAsync(
            Arg.Is<IssueTicketRequest>(r => r.HoldId == "hold_123" && r.EventId == null
                && r.FullName == "Alice Smith"),
            Arg.Any<CancellationToken>());
        await _vendor.DidNotReceive().VoidIssuedTicketAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        // Only the new Valid row is written (original is already Void), keeping the ORIGINAL €200 price.
        await _ticketRepo.Received(1).UpsertAttendeesAsync(
            Arg.Is<IReadOnlyList<TicketAttendee>>(list =>
                list.Count == 1 && list[0].VendorTicketId == "tt_retry"
                && list[0].Status == TicketAttendeeStatus.Valid && list[0].Price == 200m
                && list[0].MatchedUserId == _receiverId),
            CancellationToken.None);
        req.Status.Should().Be(TicketTransferStatus.Approved);
        req.VendorResult.Should().Be(TicketTransferVendorResult.Succeeded);
        req.NewVendorTicketId.Should().Be("tt_retry");
        req.VendorHoldId.Should().BeNull(); // consumed
        _emailMessages.Received(2).TicketTransferDecision(
            Arg.Any<string>(), Arg.Any<string>(), true, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task RetryReissue_StillFails_StaysPartial_KeepsHold_NoEmails()
    {
        var req = MakePending(Guid.NewGuid());
        req.VendorResult = TicketTransferVendorResult.VoidSucceededIssueFailed;
        req.VendorHoldId = "hold_123";
        _transferRepo.GetByIdAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);
        _ticketRepo.GetAttendeeByIdAsync(_attendeeId, Arg.Any<CancellationToken>())
            .Returns(MakeAttendee(_attendeeId, _orderId, _senderId, TicketAttendeeStatus.Void));
        _vendor.IssueTicketAsync(Arg.Any<IssueTicketRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TicketVendorWriteException("still down", TicketVendorFailureKind.Transient));

        var act = () => _service.RetryReissueAsync(req.Id, _adminId, null, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        req.Status.Should().Be(TicketTransferStatus.Pending);
        req.VendorResult.Should().Be(TicketTransferVendorResult.VoidSucceededIssueFailed);
        req.VendorHoldId.Should().Be("hold_123"); // retained so it can be retried again
        _emailMessages.DidNotReceive().TicketTransferDecision(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task RetryReissue_Throws_WhenNotPartial()
    {
        var req = MakePending(Guid.NewGuid()); // VendorResult NotAttempted
        _transferRepo.GetByIdAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);

        var act = () => _service.RetryReissueAsync(req.Id, _adminId, null, Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();
        await _vendor.DidNotReceive().IssueTicketAsync(
            Arg.Any<IssueTicketRequest>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RetryReissue_Throws_WhenNoHoldRecorded()
    {
        var req = MakePending(Guid.NewGuid());
        req.VendorResult = TicketTransferVendorResult.VoidSucceededIssueFailed;
        req.VendorHoldId = null;
        _transferRepo.GetByIdAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);

        var act = () => _service.RetryReissueAsync(req.Id, _adminId, null, Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();
        await _vendor.DidNotReceive().IssueTicketAsync(
            Arg.Any<IssueTicketRequest>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Process_Throws_WhenAlreadyPartiallyProcessed_NoSecondVoid()
    {
        // Re-processing a VoidSucceededIssueFailed request would void the already-voided ticket
        // again and overwrite the partial state — guard against it.
        var svc = CreateService();
        var req = MakePending(Guid.NewGuid());
        req.VendorResult = TicketTransferVendorResult.VoidSucceededIssueFailed;
        _transferRepo.GetByIdAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);

        var act = () => svc.ProcessTransferAsync(req.Id, _adminId, null, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _vendor.DidNotReceive().VoidIssuedTicketAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        req.VendorResult.Should().Be(TicketTransferVendorResult.VoidSucceededIssueFailed); // not overwritten
    }

    [HumansFact]
    public async Task Process_BoundsVendorMessage_WhenVendorErrorBodyIsHuge()
    {
        // VendorMessage is capped at 2000 chars; a huge TT error body must not blow the column
        // (which would make the diagnostic UpdateAsync throw and lose the partial record).
        var svc = CreateService();
        var req = MakePending(Guid.NewGuid());
        _transferRepo.GetByIdAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);
        StubAttendee(TicketAttendeeStatus.Valid, _senderId);

        _vendor.VoidIssuedTicketAsync("tkt_original", true, Arg.Any<CancellationToken>())
            .Returns(new VoidIssuedTicketResult("tkt_original", "hold_123"));
        _vendor.IssueTicketAsync(Arg.Any<IssueTicketRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TicketVendorWriteException(new string('x', 5000), TicketVendorFailureKind.Validation));

        var act = () => svc.ProcessTransferAsync(req.Id, _adminId, null, Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();

        req.VendorResult.Should().Be(TicketTransferVendorResult.VoidSucceededIssueFailed);
        req.VendorMessage!.Length.Should().BeLessThanOrEqualTo(2000);
    }

    // ── RejectAsync (cancel with reason) ────────────────────────────────────────

    [HumansFact]
    public async Task Reject_RequiresReason()
    {
        var req = MakePending(Guid.NewGuid());
        _transferRepo.GetByIdAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);

        var act = () => _service.RejectAsync(req.Id, _adminId, "   ", Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task Reject_SetsRejected_StoresReason_EmailsBothParties()
    {
        var req = MakePending(Guid.NewGuid());
        _transferRepo.GetByIdAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);
        StubAttendee(TicketAttendeeStatus.Valid, _senderId);

        await _service.RejectAsync(req.Id, _adminId, "duplicate request", Xunit.TestContext.Current.CancellationToken);

        req.Status.Should().Be(TicketTransferStatus.Rejected);
        req.AdminNotes.Should().Be("duplicate request");
        _emailMessages.Received(2).TicketTransferDecision(
            Arg.Any<string>(), Arg.Any<string>(), false, Arg.Any<string>(), Arg.Any<string>(),
            "duplicate request", Arg.Any<string?>());
    }

    [HumansFact]
    public async Task Reject_Throws_WhenVoidAlreadyCommitted()
    {
        var req = MakePending(Guid.NewGuid());
        req.VendorResult = TicketTransferVendorResult.VoidSucceededIssueFailed;
        _transferRepo.GetByIdAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);

        // Already voided at the vendor — admin can't reject it away; must finish + mark successful.
        var act = () => _service.RejectAsync(req.Id, _adminId, "no longer needed", Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();
        req.Status.Should().Be(TicketTransferStatus.Pending);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private TicketTransferService CreateService() =>
        new(
            _transferRepo,
            _ticketRepo,
            _vendor,
            _userService,
            _userEmailService,
            _emailService,
            _emailMessages,
            _auditLog,
            _cacheInvalidator,
            _clock,
            NullLogger<TicketTransferService>.Instance);

    private void StubAttendee(
        TicketAttendeeStatus status, Guid attendeeMatchedUserId, Instant? checkedInAt = null)
    {
        _ticketRepo.GetAttendeeByIdAsync(_attendeeId, Arg.Any<CancellationToken>())
            .Returns(MakeAttendee(_attendeeId, _orderId, attendeeMatchedUserId, status, checkedInAt));
    }

    private static User MakeUser(Guid id, string displayName) => new()
    {
        Id = id,
        DisplayName = displayName,
        Email = $"{displayName.ToLowerInvariant().Replace(" ", "")}@example.com",
        UserName = $"{displayName.ToLowerInvariant().Replace(" ", "")}@example.com",
        NormalizedEmail = $"{displayName.ToLowerInvariant().Replace(" ", "")}@EXAMPLE.COM",
        NormalizedUserName = $"{displayName.ToLowerInvariant().Replace(" ", "")}@EXAMPLE.COM",
    };

    private static UserInfo WrapInUserInfo(User user, Profile? profile) => UserInfo.Create(
        user: user,
        userEmails: [],
        eventParticipations: [],
        externalLogins: [],
        profile: profile,
        contactFields: [],
        profileLanguages: [],
        volunteerHistory: [],
        communicationPreferences: []);

    private static TicketAttendee MakeAttendee(
        Guid id, Guid orderId, Guid attendeeMatchedUserId, TicketAttendeeStatus status,
        Instant? checkedInAt = null)
    {
        // Buyer-fallback removed in nobodies-collective/Humans#856.
        // Ownership is determined by TicketAttendee.MatchedUserId only.
        var order = new TicketOrder
        {
            Id = orderId,
            VendorOrderId = "ord_test",
            BuyerName = "Buyer",
            BuyerEmail = "buyer@example.com",
            TotalAmount = 200m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            VendorEventId = "ev_test",
            PurchasedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            MatchedUserId = attendeeMatchedUserId,
        };

        return new TicketAttendee
        {
            Id = id,
            VendorTicketId = "tkt_original",
            TicketOrderId = orderId,
            TicketOrder = order,
            AttendeeName = "Ticket Holder",
            AttendeeEmail = "holder@example.com",
            TicketTypeName = "Full Week",
            Price = 200m,
            Status = status,
            CheckedInAt = checkedInAt,
            VendorEventId = "ev_test",
            SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            MatchedUserId = attendeeMatchedUserId,
        };
    }

    private static TicketTransferRequest MakePending(Guid id) =>
        new()
        {
            Id = id,
            OriginalTicketAttendeeId = _attendeeId,
            SenderUserId = _senderId,
            ReceiverUserId = _receiverId,
            ReceiverLegalName = "Alice Smith",
            ReceiverEmail = "alice@example.com",
            SenderReason = "Going abroad",
            Status = TicketTransferStatus.Pending,
            RequestedAt = _now,
        };
}

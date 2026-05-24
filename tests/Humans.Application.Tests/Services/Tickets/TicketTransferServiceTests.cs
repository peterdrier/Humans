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
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();

    private readonly TicketTransferService _service;

    public TicketTransferServiceTests()
    {
        _service = new TicketTransferService(
            _transferRepo,
            _ticketRepo,
            _userService,
            _userEmailService,
            _emailService,
            _auditLog,
            _clock,
            NullLogger<TicketTransferService>.Instance);

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

        var confirm = await _service.GetConfirmationAsync(_attendeeId, _receiverId, _senderId);

        confirm.Should().NotBeNull();
        confirm!.ReceiverLegalName.Should().Be("Alice Smith");
        confirm.ReceiverEmail.Should().Be("alice@example.com");
        confirm.VendorTicketId.Should().Be("tkt_original");
    }

    [HumansFact]
    public async Task GetConfirmation_Null_WhenReceiverIsSender()
    {
        StubAttendee(TicketAttendeeStatus.Valid, _senderId);
        var confirm = await _service.GetConfirmationAsync(_attendeeId, _senderId, _senderId);
        confirm.Should().BeNull();
    }

    [HumansFact]
    public async Task GetConfirmation_Null_WhenNotOwner()
    {
        StubAttendee(TicketAttendeeStatus.Valid, Guid.NewGuid());
        var confirm = await _service.GetConfirmationAsync(_attendeeId, _receiverId, _senderId);
        confirm.Should().BeNull();
    }

    [HumansFact]
    public async Task GetConfirmation_Null_WhenNotValid()
    {
        StubAttendee(TicketAttendeeStatus.Void, _senderId);
        var confirm = await _service.GetConfirmationAsync(_attendeeId, _receiverId, _senderId);
        confirm.Should().BeNull();
    }

    // ── CreateRequestAsync ──────────────────────────────────────────────────────

    [HumansFact]
    public async Task CreateRequest_Persists_Audits_AndEmailsSenderAndTeam()
    {
        StubAttendee(TicketAttendeeStatus.Valid, _senderId);

        await _service.CreateRequestAsync(
            new TicketTransferRequestDto(_attendeeId, _receiverId, "Going abroad"), _senderId);

        await _transferRepo.Received(1).AddAsync(
            Arg.Is<TicketTransferRequest>(r =>
                r.Status == TicketTransferStatus.Pending
                && r.ReceiverLegalName == "Alice Smith"
                && r.ReceiverEmail == "alice@example.com"),
            Arg.Any<CancellationToken>());
        await _auditLog.Received(1).LogAsync(
            AuditAction.TicketTransferRequested, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), _senderId, _receiverId, Arg.Any<string>());
        await _emailService.Received(1).SendTicketTransferRequestedAsync(
            "bob@example.com", Arg.Any<string>(), "Alice Smith", Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _emailService.Received(1).SendTicketTransferTeamNotificationAsync(
            Arg.Any<string>(), "Alice Smith", "alice@example.com", Arg.Any<string>(),
            "Going abroad", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateRequest_StillNotifiesTeam_WhenSenderEmailFails()
    {
        StubAttendee(TicketAttendeeStatus.Valid, _senderId);
        _emailService.SendTicketTransferRequestedAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("smtp down")));

        // Must not throw (request is already persisted) and the team must still be alerted.
        await _service.CreateRequestAsync(
            new TicketTransferRequestDto(_attendeeId, _receiverId, "x"), _senderId);

        await _emailService.Received(1).SendTicketTransferTeamNotificationAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateRequest_Throws_WhenReceiverIsSender()
    {
        var act = () => _service.CreateRequestAsync(
            new TicketTransferRequestDto(_attendeeId, _senderId, "x"), _senderId);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task CreateRequest_Throws_WhenNotOwner()
    {
        StubAttendee(TicketAttendeeStatus.Valid, Guid.NewGuid());
        var act = () => _service.CreateRequestAsync(
            new TicketTransferRequestDto(_attendeeId, _receiverId, "x"), _senderId);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task CreateRequest_Throws_WhenNotValid()
    {
        StubAttendee(TicketAttendeeStatus.Void, _senderId);
        var act = () => _service.CreateRequestAsync(
            new TicketTransferRequestDto(_attendeeId, _receiverId, "x"), _senderId);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task CreateRequest_Throws_WhenDuplicatePending()
    {
        StubAttendee(TicketAttendeeStatus.Valid, _senderId);
        _transferRepo.GetBySenderAsync(_senderId, Arg.Any<CancellationToken>())
            .Returns(new[] { MakePending(Guid.NewGuid()) });

        var act = () => _service.CreateRequestAsync(
            new TicketTransferRequestDto(_attendeeId, _receiverId, "x"), _senderId);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── CancelAsync ─────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Cancel_SetsCancelled_ForSender()
    {
        var req = MakePending(Guid.NewGuid());
        _transferRepo.GetByIdAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);

        await _service.CancelAsync(req.Id, _senderId);

        req.Status.Should().Be(TicketTransferStatus.Cancelled);
        await _auditLog.Received(1).LogAsync(
            AuditAction.TicketTransferCancelled, Arg.Any<string>(), req.Id, Arg.Any<string>(), _senderId);
    }

    [HumansFact]
    public async Task Cancel_Throws_WhenNotSender()
    {
        var req = MakePending(Guid.NewGuid());
        _transferRepo.GetByIdAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);

        var act = () => _service.CancelAsync(req.Id, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── ApproveAsync (mark successful) ──────────────────────────────────────────

    [HumansFact]
    public async Task Approve_MarksApproved_NoVendor_EmailsBothParties()
    {
        var req = MakePending(Guid.NewGuid());
        _transferRepo.GetByIdAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);
        StubAttendee(TicketAttendeeStatus.Valid, _senderId);

        await _service.ApproveAsync(req.Id, _adminId, "looks good");

        req.Status.Should().Be(TicketTransferStatus.Approved);
        req.DecidedByUserId.Should().Be(_adminId);
        await _auditLog.Received(1).LogAsync(
            AuditAction.TicketTransferApproved, Arg.Any<string>(), req.Id, Arg.Any<string>(),
            _adminId, _senderId, Arg.Any<string>());
        // Sender + Receiver each get a "successful" decision email.
        await _emailService.Received(2).SendTicketTransferDecisionAsync(
            Arg.Any<string>(), Arg.Any<string>(), true, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ── RejectAsync (cancel with reason) ────────────────────────────────────────

    [HumansFact]
    public async Task Reject_RequiresReason()
    {
        var req = MakePending(Guid.NewGuid());
        _transferRepo.GetByIdAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);

        var act = () => _service.RejectAsync(req.Id, _adminId, "   ");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task Reject_SetsRejected_StoresReason_EmailsBothParties()
    {
        var req = MakePending(Guid.NewGuid());
        _transferRepo.GetByIdAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);
        StubAttendee(TicketAttendeeStatus.Valid, _senderId);

        await _service.RejectAsync(req.Id, _adminId, "duplicate request");

        req.Status.Should().Be(TicketTransferStatus.Rejected);
        req.AdminNotes.Should().Be("duplicate request");
        await _emailService.Received(2).SendTicketTransferDecisionAsync(
            Arg.Any<string>(), Arg.Any<string>(), false, Arg.Any<string>(), Arg.Any<string>(),
            "duplicate request", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private void StubAttendee(TicketAttendeeStatus status, Guid orderMatchedUserId)
    {
        _ticketRepo.GetAttendeeByIdAsync(_attendeeId, Arg.Any<CancellationToken>())
            .Returns(MakeAttendee(_attendeeId, _orderId, orderMatchedUserId, status));
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
        Guid id, Guid orderId, Guid orderMatchedUserId, TicketAttendeeStatus status)
    {
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
            MatchedUserId = orderMatchedUserId,
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
            VendorEventId = "ev_test",
            SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
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

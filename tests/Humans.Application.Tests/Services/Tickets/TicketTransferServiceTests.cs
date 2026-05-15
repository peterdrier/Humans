using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Configuration;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profiles;
using Humans.Application.Services.Tickets;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Humans.Application.Tests.Services.Tickets;

/// <summary>
/// Pure unit tests for <see cref="TicketTransferService"/> — all dependencies
/// are NSubstitute substitutes. No database or EF context required.
/// </summary>
public sealed class TicketTransferServiceTests
{
    // ── Fixed clock ────────────────────────────────────────────────────────────
    private static readonly Instant _now = Instant.FromUtc(2026, 5, 5, 10, 0);
    private readonly FakeClock _clock = new(_now);

    // ── IDs used across tests ─────────────────────────────────────────────────
    private static readonly Guid _senderId = Guid.NewGuid();
    private static readonly Guid _receiverId = Guid.NewGuid();
    private static readonly Guid _adminId = Guid.NewGuid();
    private static readonly Guid _attendeeId = Guid.NewGuid();
    private static readonly Guid _orderId = Guid.NewGuid();

    // ── Substitutes ────────────────────────────────────────────────────────────
    private readonly ITicketTransferRepository _transferRepo = Substitute.For<ITicketTransferRepository>();
    private readonly ITicketRepository _ticketRepo = Substitute.For<ITicketRepository>();
    private readonly ITicketVendorService _vendor = Substitute.For<ITicketVendorService>();
    private readonly ITicketQueryService _ticketQueryService = Substitute.For<ITicketQueryService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IProfileService _profileService = Substitute.For<IProfileService>();
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();

    private readonly TicketTransferService _service;

    public TicketTransferServiceTests()
    {
        _service = new TicketTransferService(
            _transferRepo,
            _ticketRepo,
            _vendor,
            _ticketQueryService,
            _userService,
            _userEmailService,
            _profileService,
            _auditLog,
            _clock,
            NullLogger<TicketTransferService>.Instance);

        // Default: Receiver user exists with a complete profile (BurnerName +
        // FirstName + LastName populated — required for transfer recipient).
        _userService.GetByIdAsync(_receiverId, Arg.Any<CancellationToken>())
            .Returns(MakeUser(_receiverId, "Alice"));
        _userService.GetUserInfoAsync(_receiverId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(
                MakeUser(_receiverId, "Alice"),
                new Profile { BurnerName = "Alice", FirstName = "Alice", LastName = "Smith" }));

        // Default: Receiver has primary email
        _userEmailService.GetPrimaryEmailAsync(_receiverId, Arg.Any<CancellationToken>())
            .Returns("alice@example.com");

        // Default: name/burner search returns empty
        _profileService.SearchProfilesAsync(
                Arg.Any<string>(), Arg.Any<PersonSearchFields>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<HumanSearchResult>());

        // Default: no existing transfers from the sender (CreateRequestAsync's
        // duplicate-pending guard hits this on every Submit).
        _transferRepo.GetBySenderAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TicketTransferRequest>());
    }

    // ============================================================================
    // LookupReceiversAsync
    // ============================================================================

    [HumansFact]
    public async Task LookupReceiversAsync_EmptyOnWhitespace()
    {
        var result = await _service.LookupReceiversAsync("   ", _senderId);
        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task LookupReceiversAsync_EmailHeuristic_ReturnsSingleCard()
    {
        var userId = Guid.NewGuid();
        _userEmailService.GetUserIdByExactEmailAsync("alice@example.com", Arg.Any<CancellationToken>())
            .Returns(userId);
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(MakeUser(userId, "Alice"));
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(
                MakeUser(userId, "Alice"),
                new Profile { BurnerName = "Alice", FirstName = "Alice", LastName = "Smith" }));
        _userEmailService.GetPrimaryEmailAsync(userId, Arg.Any<CancellationToken>())
            .Returns("alice@example.com");
        _profileService.GetProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns((Profile?)null);

        var result = await _service.LookupReceiversAsync("alice@example.com", _senderId);

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(userId);
        result[0].DisplayName.Should().Be("Alice");
    }

    [HumansFact]
    public async Task LookupReceiversAsync_EmailHeuristic_EmptyWhenMatchIsSender()
    {
        _userEmailService.GetUserIdByExactEmailAsync("self@example.com", Arg.Any<CancellationToken>())
            .Returns(_senderId);

        var result = await _service.LookupReceiversAsync("self@example.com", _senderId);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task LookupReceiversAsync_EmailHeuristic_EmptyWhenNoMatch()
    {
        _userEmailService.GetUserIdByExactEmailAsync("nobody@example.com", Arg.Any<CancellationToken>())
            .Returns((Guid?)null);

        var result = await _service.LookupReceiversAsync("nobody@example.com", _senderId);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task LookupReceiversAsync_BurnerName_SingleMatch_ReturnsOne()
    {
        var userId = Guid.NewGuid();
        _profileService.SearchProfilesAsync(
                Arg.Any<string>(), Arg.Any<PersonSearchFields>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeSearchResult(userId, "Sparkle Person") });
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(MakeUser(userId, "Sparkle Person"));
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(
                MakeUser(userId, "Sparkle Person"),
                new Profile { BurnerName = "Sparkle Person", FirstName = "Sparkle", LastName = "Person" }));
        _userEmailService.GetPrimaryEmailAsync(userId, Arg.Any<CancellationToken>())
            .Returns("sp@example.com");
        _profileService.GetProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns((Profile?)null);

        var result = await _service.LookupReceiversAsync("sparkle", _senderId);

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(userId);
    }

    [HumansFact]
    public async Task LookupReceiversAsync_BurnerName_AmbiguousMatch_ReturnsAll()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        _profileService.SearchProfilesAsync(
                Arg.Any<string>(), Arg.Any<PersonSearchFields>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                MakeSearchResult(aId, "A"),
                MakeSearchResult(bId, "B"),
            });
        _userService.GetByIdAsync(aId, Arg.Any<CancellationToken>()).Returns(MakeUser(aId, "A"));
        _userService.GetByIdAsync(bId, Arg.Any<CancellationToken>()).Returns(MakeUser(bId, "B"));
        _userService.GetUserInfoAsync(aId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(
                MakeUser(aId, "A"),
                new Profile { BurnerName = "A", FirstName = "Aaa", LastName = "Aaa" }));
        _userService.GetUserInfoAsync(bId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(
                MakeUser(bId, "B"),
                new Profile { BurnerName = "B", FirstName = "Bbb", LastName = "Bbb" }));
        _userEmailService.GetPrimaryEmailAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _profileService.GetProfileAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Profile?)null);

        var result = await _service.LookupReceiversAsync("popular", _senderId);

        result.Should().HaveCount(2);
        result.Select(r => r.UserId).Should().BeEquivalentTo(new[] { aId, bId });
    }

    [HumansFact]
    public async Task LookupReceiversAsync_BurnerName_ExcludesSender()
    {
        _profileService.SearchProfilesAsync(
                Arg.Any<string>(), Arg.Any<PersonSearchFields>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeSearchResult(_senderId, "Me") });

        var result = await _service.LookupReceiversAsync("me", _senderId);

        result.Should().BeEmpty();
    }

    // ============================================================================
    // CreateRequestAsync
    // ============================================================================

    [HumansFact]
    public async Task CreateRequestAsync_HappyPath_ReturnsPendingRow()
    {
        var attendee = MakeAttendee(_attendeeId, _orderId, _senderId, TicketAttendeeStatus.Valid);
        _ticketRepo.GetAttendeeByIdAsync(_attendeeId, Arg.Any<CancellationToken>())
            .Returns(attendee);
        _userService.GetByIdAsync(_senderId, Arg.Any<CancellationToken>())
            .Returns(MakeUser(_senderId, "Bob"));

        var dto = new TicketTransferRequestDto(_attendeeId, _receiverId, "Going abroad");

        var row = await _service.CreateRequestAsync(dto, _senderId);

        row.Status.Should().Be(TicketTransferStatus.Pending);
        row.ReceiverUserId.Should().Be(_receiverId);
        row.ReceiverLegalName.Should().Be("Alice Smith");
        await _transferRepo.Received(1).AddAsync(
            Arg.Is<TicketTransferRequest>(r => r.Status == TicketTransferStatus.Pending),
            Arg.Any<CancellationToken>());
        await _auditLog.Received(1).LogAsync(
            AuditAction.TicketTransferRequested,
            nameof(TicketTransferRequest),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            _senderId,
            _receiverId,
            nameof(User));
    }

    [HumansFact]
    public async Task CreateRequestAsync_ThrowsWhenReceiverEqualsSender()
    {
        var dto = new TicketTransferRequestDto(_attendeeId, _senderId, "test");

        var act = () => _service.CreateRequestAsync(dto, _senderId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*yourself*");
    }

    [HumansFact]
    public async Task CreateRequestAsync_ThrowsWhenAttendeeNotFound()
    {
        _ticketRepo.GetAttendeeByIdAsync(_attendeeId, Arg.Any<CancellationToken>())
            .Returns((TicketAttendee?)null);

        var dto = new TicketTransferRequestDto(_attendeeId, _receiverId, "test");

        var act = () => _service.CreateRequestAsync(dto, _senderId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Attendee not found*");
    }

    [HumansFact]
    public async Task CreateRequestAsync_ThrowsWhenSenderDoesNotOwnOrder()
    {
        var otherId = Guid.NewGuid();
        var attendee = MakeAttendee(_attendeeId, _orderId, otherId, TicketAttendeeStatus.Valid);
        _ticketRepo.GetAttendeeByIdAsync(_attendeeId, Arg.Any<CancellationToken>())
            .Returns(attendee);

        var dto = new TicketTransferRequestDto(_attendeeId, _receiverId, "test");

        var act = () => _service.CreateRequestAsync(dto, _senderId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*currently hold*");
    }

    [HumansFact]
    public async Task CreateRequestAsync_ThrowsWhenAttendeeStatusNotValid()
    {
        var attendee = MakeAttendee(_attendeeId, _orderId, _senderId, TicketAttendeeStatus.Void);
        _ticketRepo.GetAttendeeByIdAsync(_attendeeId, Arg.Any<CancellationToken>())
            .Returns(attendee);

        var dto = new TicketTransferRequestDto(_attendeeId, _receiverId, "test");

        var act = () => _service.CreateRequestAsync(dto, _senderId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Valid tickets*");
    }

    [HumansFact]
    public async Task CreateRequestAsync_ThrowsWhenReceiverUserNotFound()
    {
        var attendee = MakeAttendee(_attendeeId, _orderId, _senderId, TicketAttendeeStatus.Valid);
        _ticketRepo.GetAttendeeByIdAsync(_attendeeId, Arg.Any<CancellationToken>())
            .Returns(attendee);
        _userService.GetByIdAsync(_receiverId, Arg.Any<CancellationToken>())
            .Returns((User?)null);
        _userService.GetUserInfoAsync(_receiverId, Arg.Any<CancellationToken>())
            .Returns((UserInfo?)null);

        var dto = new TicketTransferRequestDto(_attendeeId, _receiverId, "test");

        var act = () => _service.CreateRequestAsync(dto, _senderId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Receiver user not found*");
    }

    [HumansFact]
    public async Task CreateRequestAsync_ThrowsWhenReceiverHasNoPrimaryEmail()
    {
        var attendee = MakeAttendee(_attendeeId, _orderId, _senderId, TicketAttendeeStatus.Valid);
        _ticketRepo.GetAttendeeByIdAsync(_attendeeId, Arg.Any<CancellationToken>())
            .Returns(attendee);
        _userEmailService.GetPrimaryEmailAsync(_receiverId, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var dto = new TicketTransferRequestDto(_attendeeId, _receiverId, "test");

        var act = () => _service.CreateRequestAsync(dto, _senderId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no primary email*");
    }

    [HumansFact]
    public async Task CreateRequestAsync_ThrowsWhenReceiverMissingLegalName()
    {
        // Defense-in-depth: BuildReceiverCardAsync filters at the lookup layer,
        // but Submit accepts a direct POST. Recipients without a legal name on
        // file cannot receive transfers — the transfer snapshot writes the name
        // onto the reissued ticket. Wording matches the not-found case so a
        // tampered POST learns nothing about why the recipient was rejected.
        var attendee = MakeAttendee(_attendeeId, _orderId, _senderId, TicketAttendeeStatus.Valid);
        _ticketRepo.GetAttendeeByIdAsync(_attendeeId, Arg.Any<CancellationToken>())
            .Returns(attendee);
        _userService.GetUserInfoAsync(_receiverId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(MakeUser(_receiverId, "Alice"), profile: null));

        var dto = new TicketTransferRequestDto(_attendeeId, _receiverId, "test");

        var act = () => _service.CreateRequestAsync(dto, _senderId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Receiver user not found*");
        await _transferRepo.DidNotReceive().AddAsync(
            Arg.Any<TicketTransferRequest>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateRequestAsync_ThrowsWhenPendingTransferAlreadyExistsForAttendee()
    {
        var attendee = MakeAttendee(_attendeeId, _orderId, _senderId, TicketAttendeeStatus.Valid);
        _ticketRepo.GetAttendeeByIdAsync(_attendeeId, Arg.Any<CancellationToken>())
            .Returns(attendee);
        _userService.GetUserInfoAsync(_receiverId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(
                MakeUser(_receiverId, "Alice"),
                new Profile { BurnerName = "Alice", FirstName = "Alice", LastName = "Smith" }));
        _transferRepo.GetBySenderAsync(_senderId, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new TicketTransferRequest
                {
                    Id = Guid.NewGuid(),
                    OriginalTicketAttendeeId = _attendeeId,
                    SenderUserId = _senderId,
                    ReceiverUserId = Guid.NewGuid(),
                    Status = TicketTransferStatus.Pending,
                    RequestedAt = _now,
                },
            });

        var dto = new TicketTransferRequestDto(_attendeeId, _receiverId, "test");

        var act = () => _service.CreateRequestAsync(dto, _senderId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*pending transfer*");
        await _transferRepo.DidNotReceive().AddAsync(
            Arg.Any<TicketTransferRequest>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateRequestAsync_SnapshotsReceiverLegalNameFromProfileFullName()
    {
        var attendee = MakeAttendee(_attendeeId, _orderId, _senderId, TicketAttendeeStatus.Valid);
        _ticketRepo.GetAttendeeByIdAsync(_attendeeId, Arg.Any<CancellationToken>())
            .Returns(attendee);
        _userService.GetByIdAsync(_senderId, Arg.Any<CancellationToken>())
            .Returns(MakeUser(_senderId, "Bob"));
        _userService.GetUserInfoAsync(_receiverId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(
                MakeUser(_receiverId, "Alice"),
                new Profile { BurnerName = "Alice", FirstName = "Alice", LastName = "Smith" }));

        var dto = new TicketTransferRequestDto(_attendeeId, _receiverId, "Going abroad");

        var row = await _service.CreateRequestAsync(dto, _senderId);

        row.ReceiverLegalName.Should().Be("Alice Smith");
        await _transferRepo.Received(1).AddAsync(
            Arg.Is<TicketTransferRequest>(r => r.ReceiverLegalName == "Alice Smith"),
            Arg.Any<CancellationToken>());
    }

    // ============================================================================
    // CancelAsync
    // ============================================================================

    [HumansFact]
    public async Task CancelAsync_HappyPath_SetsCancelledAndAudit()
    {
        var transferId = Guid.NewGuid();
        var request = MakePendingRequest(transferId, _senderId, _receiverId);
        _transferRepo.GetByIdAsync(transferId, Arg.Any<CancellationToken>())
            .Returns(request);

        await _service.CancelAsync(transferId, _senderId);

        request.Status.Should().Be(TicketTransferStatus.Cancelled);
        request.DecidedAt.Should().Be(_now);
        await _transferRepo.Received(1).UpdateAsync(request, Arg.Any<CancellationToken>());
        await _auditLog.Received(1).LogAsync(
            AuditAction.TicketTransferCancelled,
            nameof(TicketTransferRequest),
            transferId,
            Arg.Any<string>(),
            _senderId);
    }

    [HumansFact]
    public async Task CancelAsync_ThrowsWhenNotPending()
    {
        var transferId = Guid.NewGuid();
        var request = MakePendingRequest(transferId, _senderId, _receiverId);
        request.Status = TicketTransferStatus.Approved;
        _transferRepo.GetByIdAsync(transferId, Arg.Any<CancellationToken>())
            .Returns(request);

        var act = () => _service.CancelAsync(transferId, _senderId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Pending transfers can be cancelled*");
    }

    [HumansFact]
    public async Task CancelAsync_ThrowsWhenCallerIsNotSender()
    {
        var transferId = Guid.NewGuid();
        var request = MakePendingRequest(transferId, _senderId, _receiverId);
        _transferRepo.GetByIdAsync(transferId, Arg.Any<CancellationToken>())
            .Returns(request);

        var act = () => _service.CancelAsync(transferId, Guid.NewGuid() /* different caller */);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Sender can cancel*");
    }

    // ============================================================================
    // RejectAsync
    // ============================================================================

    [HumansFact]
    public async Task RejectAsync_HappyPath_SetsRejectedFieldsAndAudit()
    {
        var transferId = Guid.NewGuid();
        var request = MakePendingRequest(transferId, _senderId, _receiverId);
        _transferRepo.GetByIdAsync(transferId, Arg.Any<CancellationToken>())
            .Returns(request);
        WireSenderAndAttendeeForRow(request);

        var row = await _service.RejectAsync(transferId, _adminId, "Not eligible");

        request.Status.Should().Be(TicketTransferStatus.Rejected);
        request.DecidedByUserId.Should().Be(_adminId);
        request.DecidedAt.Should().Be(_now);
        request.AdminNotes.Should().Be("Not eligible");
        row.Status.Should().Be(TicketTransferStatus.Rejected);
        await _auditLog.Received(1).LogAsync(
            AuditAction.TicketTransferRejected,
            nameof(TicketTransferRequest),
            transferId,
            Arg.Is<string>(s => s.Contains("Not eligible")),
            _adminId,
            _senderId,
            nameof(User));
    }

    [HumansFact]
    public async Task RejectAsync_ThrowsWhenNotPending()
    {
        var transferId = Guid.NewGuid();
        var request = MakePendingRequest(transferId, _senderId, _receiverId);
        request.Status = TicketTransferStatus.Rejected;
        _transferRepo.GetByIdAsync(transferId, Arg.Any<CancellationToken>())
            .Returns(request);

        var act = () => _service.RejectAsync(transferId, _adminId, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Pending transfers can be decided*");
    }

    // ============================================================================
    // ApproveAsync — vendor branch: both calls succeed
    // ============================================================================

    [HumansFact]
    public async Task ApproveAsync_HappyPath_VendorSucceeds_SetsSucceededAndUpsertsBatchAndInvalidates()
    {
        var transferId = Guid.NewGuid();
        var request = MakePendingRequest(transferId, _senderId, _receiverId);
        _transferRepo.GetByIdAsync(transferId, Arg.Any<CancellationToken>())
            .Returns(request);

        var attendee = MakeAttendee(_attendeeId, _orderId, _senderId, TicketAttendeeStatus.Valid);
        request.OriginalTicketAttendee = attendee;

        _vendor.VoidIssuedTicketAsync("tkt_original", true, Arg.Any<CancellationToken>())
            .Returns(new VoidIssuedTicketResult("tkt_original", "hold_001"));
        _vendor.IssueTicketAsync(
                Arg.Is<IssueTicketRequest>(r => r.HoldId == "hold_001"),
                Arg.Any<CancellationToken>())
            .Returns(new VendorTicketDto(
                VendorTicketId: "tkt_new",
                VendorOrderId: null,
                AttendeeName: "Alice",
                AttendeeEmail: "alice@example.com",
                TicketTypeName: "Full Week",
                Price: 200m,
                Status: "valid"));

        WireSenderAndAttendeeForRow(request);

        var row = await _service.ApproveAsync(transferId, _adminId, null);

        row.Status.Should().Be(TicketTransferStatus.Approved);
        request.VendorResult.Should().Be(TicketTransferVendorResult.Succeeded);
        request.NewVendorTicketId.Should().Be("tkt_new");
        // Both attendee rows (new Receiver + voided original) must be written atomically
        // through one UpsertAttendeesAsync — never two singular UpsertAttendeeAsync calls.
        await _ticketRepo.Received(1).UpsertAttendeesAsync(
            Arg.Is<IReadOnlyList<TicketAttendee>>(list =>
                list.Count == 2
                && list.Any(a => a.VendorTicketId == "tkt_new"
                    && a.Status == TicketAttendeeStatus.Valid
                    && a.MatchedUserId == _receiverId)
                && list.Any(a => a.VendorTicketId == "tkt_original"
                    && a.Status == TicketAttendeeStatus.Void)),
            Arg.Any<CancellationToken>());
        await _ticketRepo.DidNotReceive().UpsertAttendeeAsync(Arg.Any<TicketAttendee>(), Arg.Any<CancellationToken>());
        _ticketQueryService.Received(1).InvalidateAfterTransfer(_senderId, _receiverId);
        await _auditLog.Received(1).LogAsync(
            AuditAction.TicketTransferApproved,
            nameof(TicketTransferRequest),
            transferId,
            Arg.Is<string>(s => s.Contains("TT void+reissue OK")),
            _adminId,
            _senderId,
            nameof(User));
    }

    // ============================================================================
    // ApproveAsync — vendor branch: void succeeds, issue throws
    // ============================================================================

    [HumansFact]
    public async Task ApproveAsync_VoidOk_IssueFails_VoidsLocallyAndInvalidatesSenderOnly()
    {
        var transferId = Guid.NewGuid();
        var request = MakePendingRequest(transferId, _senderId, _receiverId);
        _transferRepo.GetByIdAsync(transferId, Arg.Any<CancellationToken>())
            .Returns(request);

        var attendee = MakeAttendee(_attendeeId, _orderId, _senderId, TicketAttendeeStatus.Valid);
        request.OriginalTicketAttendee = attendee;

        _vendor.VoidIssuedTicketAsync("tkt_original", true, Arg.Any<CancellationToken>())
            .Returns(new VoidIssuedTicketResult("tkt_original", "hold_002"));
        _vendor.IssueTicketAsync(Arg.Any<IssueTicketRequest>(), Arg.Any<CancellationToken>())
            .Throws(new TicketVendorWriteException("Sold out", TicketVendorFailureKind.Validation));

        WireSenderAndAttendeeForRow(request);

        var row = await _service.ApproveAsync(transferId, _adminId, null);

        row.Status.Should().Be(TicketTransferStatus.Approved);
        request.VendorResult.Should().Be(TicketTransferVendorResult.VoidSucceededIssueFailed);
        request.VendorMessage.Should().StartWith("Issue failed");
        attendee.Status.Should().Be(TicketAttendeeStatus.Void);
        await _ticketRepo.Received(1).UpsertAttendeeAsync(Arg.Any<TicketAttendee>(), Arg.Any<CancellationToken>());
        _ticketQueryService.Received(1).InvalidateAfterTransfer(_senderId, null);
        await _auditLog.Received(1).LogAsync(
            AuditAction.TicketTransferApproved,
            nameof(TicketTransferRequest),
            transferId,
            Arg.Is<string>(s => s.Contains("manual reissue needed")),
            _adminId,
            _senderId,
            nameof(User));
    }

    // ============================================================================
    // ApproveAsync — vendor branch: void throws
    // ============================================================================

    [HumansFact]
    public async Task ApproveAsync_VoidFails_SetsFailed_OptionCFallback_NoLocalMutation()
    {
        var transferId = Guid.NewGuid();
        var request = MakePendingRequest(transferId, _senderId, _receiverId);
        _transferRepo.GetByIdAsync(transferId, Arg.Any<CancellationToken>())
            .Returns(request);

        var attendee = MakeAttendee(_attendeeId, _orderId, _senderId, TicketAttendeeStatus.Valid);
        request.OriginalTicketAttendee = attendee;

        _vendor.VoidIssuedTicketAsync("tkt_original", true, Arg.Any<CancellationToken>())
            .Throws(new TicketVendorWriteException("TT 500", TicketVendorFailureKind.Transient));

        WireSenderAndAttendeeForRow(request);

        var row = await _service.ApproveAsync(transferId, _adminId, null);

        row.Status.Should().Be(TicketTransferStatus.Approved);
        request.VendorResult.Should().Be(TicketTransferVendorResult.Failed);
        request.VendorMessage.Should().StartWith("Void failed");
        await _ticketRepo.DidNotReceive().UpsertAttendeeAsync(Arg.Any<TicketAttendee>(), Arg.Any<CancellationToken>());
        _ticketQueryService.DidNotReceive().InvalidateAfterTransfer(Arg.Any<Guid>(), Arg.Any<Guid?>());
        await _auditLog.Received(1).LogAsync(
            AuditAction.TicketTransferApproved,
            nameof(TicketTransferRequest),
            transferId,
            Arg.Is<string>(s => s.Contains("Option-C fallback")),
            _adminId,
            _senderId,
            nameof(User));
    }

    [HumansFact]
    public async Task ApproveAsync_ThrowsWhenNotPending()
    {
        var transferId = Guid.NewGuid();
        var request = MakePendingRequest(transferId, _senderId, _receiverId);
        request.Status = TicketTransferStatus.Cancelled;
        _transferRepo.GetByIdAsync(transferId, Arg.Any<CancellationToken>())
            .Returns(request);

        var act = () => _service.ApproveAsync(transferId, _adminId, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Pending transfers can be decided*");
    }

    // ============================================================================
    // GetMyAttendeesAsync
    // ============================================================================

    [HumansFact]
    public async Task GetMyAttendeesAsync_ComposesFlagsFromAttendeesAndPendingTransfers()
    {
        var ownAttendeeId = Guid.NewGuid();
        var pendingTransferAttendeeId = Guid.NewGuid();
        var transferredInAttendeeId = Guid.NewGuid();
        var pendingTransferId = Guid.NewGuid();

        var ownAttendee = MakeAttendee(ownAttendeeId, _orderId, _senderId, TicketAttendeeStatus.Valid);
        ownAttendee.AttendeeName = "Bob";
        var pendingAttendee = MakeAttendee(pendingTransferAttendeeId, _orderId, _senderId, TicketAttendeeStatus.Valid);
        pendingAttendee.AttendeeName = "Carol";
        var transferredInAttendee = MakeAttendee(transferredInAttendeeId, Guid.NewGuid(), Guid.NewGuid(), TicketAttendeeStatus.Valid);
        transferredInAttendee.AttendeeName = "Alice";
        transferredInAttendee.MatchedUserId = _senderId; // received via prior transfer

        _ticketRepo.GetAttendeesVisibleToUserAsync(_senderId, Arg.Any<CancellationToken>())
            .Returns(new[] { ownAttendee, pendingAttendee, transferredInAttendee });

        _transferRepo.GetBySenderAsync(_senderId, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new TicketTransferRequest
                {
                    Id = pendingTransferId,
                    OriginalTicketAttendeeId = pendingTransferAttendeeId,
                    SenderUserId = _senderId,
                    ReceiverUserId = _receiverId,
                    Status = TicketTransferStatus.Pending,
                    RequestedAt = _now,
                },
            });

        var rows = await _service.GetMyAttendeesAsync(_senderId);

        rows.Should().HaveCount(3);
        // Alphabetical by attendee name
        rows[0].AttendeeName.Should().Be("Alice");
        rows[1].AttendeeName.Should().Be("Bob");
        rows[2].AttendeeName.Should().Be("Carol");

        // Own valid attendee with no pending transfer → can send
        rows.Single(r => r.AttendeeId == ownAttendeeId).CanSendTransfer.Should().BeTrue();
        rows.Single(r => r.AttendeeId == ownAttendeeId).HasPendingOutgoingTransfer.Should().BeFalse();

        // Own valid attendee with a pending outgoing transfer → cannot send (already pending)
        var pendingRow = rows.Single(r => r.AttendeeId == pendingTransferAttendeeId);
        pendingRow.CanSendTransfer.Should().BeFalse();
        pendingRow.HasPendingOutgoingTransfer.Should().BeTrue();
        pendingRow.PendingTransferRequestId.Should().Be(pendingTransferId);

        // Transferred-in attendee: MatchedUserId == _senderId → ownership cascade makes them the
        // current holder, so they CAN send an onward transfer.
        rows.Single(r => r.AttendeeId == transferredInAttendeeId).CanSendTransfer.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetMyAttendeesAsync_DoesNotCrashOnDuplicatePendingRowsForSameAttendee()
    {
        // CreateRequestAsync now blocks duplicates, but the dashboard read should
        // tolerate any pre-existing strays rather than throwing on the dictionary build.
        var attendeeId = Guid.NewGuid();
        var attendee = MakeAttendee(attendeeId, _orderId, _senderId, TicketAttendeeStatus.Valid);
        _ticketRepo.GetAttendeesVisibleToUserAsync(_senderId, Arg.Any<CancellationToken>())
            .Returns(new[] { attendee });
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        _transferRepo.GetBySenderAsync(_senderId, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new TicketTransferRequest
                {
                    Id = firstId,
                    OriginalTicketAttendeeId = attendeeId,
                    SenderUserId = _senderId,
                    ReceiverUserId = _receiverId,
                    Status = TicketTransferStatus.Pending,
                    RequestedAt = _now,
                },
                new TicketTransferRequest
                {
                    Id = secondId,
                    OriginalTicketAttendeeId = attendeeId,
                    SenderUserId = _senderId,
                    ReceiverUserId = Guid.NewGuid(),
                    Status = TicketTransferStatus.Pending,
                    RequestedAt = _now,
                },
            });

        var rows = await _service.GetMyAttendeesAsync(_senderId);

        rows.Should().HaveCount(1);
        rows[0].HasPendingOutgoingTransfer.Should().BeTrue();
        rows[0].CanSendTransfer.Should().BeFalse();
    }

    // ============================================================================
    // Helpers
    // ============================================================================

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
        userEmails: Array.Empty<UserEmail>(),
        eventParticipations: Array.Empty<EventParticipation>(),
        externalLogins: Array.Empty<(string, string)>(),
        profile: profile,
        contactFields: Array.Empty<ContactField>(),
        profileLanguages: Array.Empty<ProfileLanguage>(),
        volunteerHistory: Array.Empty<VolunteerHistoryEntry>(),
        communicationPreferences: Array.Empty<CommunicationPreference>());

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
            TicketTypeName = "Full Week",
            Price = 200m,
            Status = status,
            VendorEventId = "ev_test",
            SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
        };
    }

    private static TicketTransferRequest MakePendingRequest(Guid id, Guid senderId, Guid receiverId) =>
        new()
        {
            Id = id,
            OriginalTicketAttendeeId = _attendeeId,
            SenderUserId = senderId,
            ReceiverUserId = receiverId,
            ReceiverLegalName = "Alice",
            ReceiverEmail = "alice@example.com",
            SenderReason = "Going abroad",
            Status = TicketTransferStatus.Pending,
            VendorResult = TicketTransferVendorResult.NotAttempted,
            RequestedAt = _now,
        };

    private static HumanSearchResult MakeSearchResult(Guid userId, string displayName) =>
        new(
            UserId: userId,
            ProfileId: Guid.NewGuid(),
            BurnerName: displayName,
            ProfilePictureUrl: null,
            MatchField: "Name",
            MatchSnippet: null,
            MatchedEmail: null);

    /// <summary>
    /// Wires up GetByIdAsync for Sender + attendee so BuildRowDtoAsync
    /// (called at the end of create/reject/approve) can complete without null-ref.
    /// </summary>
    private void WireSenderAndAttendeeForRow(TicketTransferRequest request)
    {
        _userService.GetByIdAsync(request.SenderUserId, Arg.Any<CancellationToken>())
            .Returns(MakeUser(request.SenderUserId, "Bob"));

        if (request.OriginalTicketAttendee is null)
        {
            var attendee = MakeAttendee(
                request.OriginalTicketAttendeeId,
                _orderId,
                request.SenderUserId,
                TicketAttendeeStatus.Valid);
            _ticketRepo.GetAttendeeByIdAsync(request.OriginalTicketAttendeeId, Arg.Any<CancellationToken>())
                .Returns(attendee);
        }
    }
}

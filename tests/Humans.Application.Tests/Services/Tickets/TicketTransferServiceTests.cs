using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Configuration;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Tickets;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    private static readonly Guid _requesterId = Guid.NewGuid();
    private static readonly Guid _recipientId = Guid.NewGuid();
    private static readonly Guid _adminId = Guid.NewGuid();
    private static readonly Guid _attendeeId = Guid.NewGuid();
    private static readonly Guid _orderId = Guid.NewGuid();

    // ── Substitutes ────────────────────────────────────────────────────────────
    private readonly ITicketTransferRepository _transferRepo = Substitute.For<ITicketTransferRepository>();
    private readonly ITicketRepository _ticketRepo = Substitute.For<ITicketRepository>();
    private readonly ITicketVendorService _vendor = Substitute.For<ITicketVendorService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IProfileService _profileService = Substitute.For<IProfileService>();
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();

    private readonly TicketTransferService _service;

    public TicketTransferServiceTests()
    {
        var settings = Options.Create(new TicketVendorSettings { EventId = "ev_test", ApiKey = "key" });

        _service = new TicketTransferService(
            _transferRepo,
            _ticketRepo,
            _vendor,
            _userService,
            _userEmailService,
            _profileService,
            _auditLog,
            settings,
            _clock,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<TicketTransferService>.Instance);

        // Default: no pending transfer for any attendee
        _transferRepo.GetPendingForAttendeeAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((TicketTransferRequest?)null);

        // Default: no matched attendees for any event (recipient conflict check passes)
        _ticketRepo.GetMatchedAttendeesForEventAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MatchedAttendeeRow>());

        // Default: recipient user exists
        _userService.GetByIdAsync(_recipientId, Arg.Any<CancellationToken>())
            .Returns(MakeUser(_recipientId, "Alice"));

        // Default: recipient has primary email
        _userEmailService.GetPrimaryEmailAsync(_recipientId, Arg.Any<CancellationToken>())
            .Returns("alice@example.com");

        // Default: name/burner search returns empty
        _profileService.SearchProfilesAsync(Arg.Any<Func<FullProfile, bool>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<HumanSearchResult>());
    }

    // ============================================================================
    // LookupRecipientsAsync
    // ============================================================================

    [HumansFact]
    public async Task LookupRecipientsAsync_EmptyOnWhitespace()
    {
        var result = await _service.LookupRecipientsAsync("   ", _requesterId);
        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task LookupRecipientsAsync_EmailHeuristic_ReturnsSingleCard()
    {
        var userId = Guid.NewGuid();
        _userEmailService.GetUserIdByExactEmailAsync("alice@example.com", Arg.Any<CancellationToken>())
            .Returns(userId);
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(MakeUser(userId, "Alice"));
        _userEmailService.GetPrimaryEmailAsync(userId, Arg.Any<CancellationToken>())
            .Returns("alice@example.com");
        _profileService.GetProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns((Profile?)null);

        var result = await _service.LookupRecipientsAsync("alice@example.com", _requesterId);

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(userId);
        result[0].DisplayName.Should().Be("Alice");
    }

    [HumansFact]
    public async Task LookupRecipientsAsync_EmailHeuristic_EmptyWhenMatchIsRequester()
    {
        _userEmailService.GetUserIdByExactEmailAsync("self@example.com", Arg.Any<CancellationToken>())
            .Returns(_requesterId);

        var result = await _service.LookupRecipientsAsync("self@example.com", _requesterId);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task LookupRecipientsAsync_EmailHeuristic_EmptyWhenNoMatch()
    {
        _userEmailService.GetUserIdByExactEmailAsync("nobody@example.com", Arg.Any<CancellationToken>())
            .Returns((Guid?)null);

        var result = await _service.LookupRecipientsAsync("nobody@example.com", _requesterId);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task LookupRecipientsAsync_BurnerName_SingleMatch_ReturnsOne()
    {
        var userId = Guid.NewGuid();
        _profileService.SearchProfilesAsync(Arg.Any<Func<FullProfile, bool>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeSearchResult(userId, "Sparkle Person") });
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(MakeUser(userId, "Sparkle Person"));
        _userEmailService.GetPrimaryEmailAsync(userId, Arg.Any<CancellationToken>())
            .Returns("sp@example.com");
        _profileService.GetProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns((Profile?)null);

        var result = await _service.LookupRecipientsAsync("sparkle", _requesterId);

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(userId);
    }

    [HumansFact]
    public async Task LookupRecipientsAsync_BurnerName_AmbiguousMatch_ReturnsAll()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        _profileService.SearchProfilesAsync(Arg.Any<Func<FullProfile, bool>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                MakeSearchResult(aId, "A"),
                MakeSearchResult(bId, "B"),
            });
        _userService.GetByIdAsync(aId, Arg.Any<CancellationToken>()).Returns(MakeUser(aId, "A"));
        _userService.GetByIdAsync(bId, Arg.Any<CancellationToken>()).Returns(MakeUser(bId, "B"));
        _userEmailService.GetPrimaryEmailAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _profileService.GetProfileAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Profile?)null);

        var result = await _service.LookupRecipientsAsync("popular", _requesterId);

        result.Should().HaveCount(2);
        result.Select(r => r.UserId).Should().BeEquivalentTo(new[] { aId, bId });
    }

    [HumansFact]
    public async Task LookupRecipientsAsync_BurnerName_ExcludesRequester()
    {
        _profileService.SearchProfilesAsync(Arg.Any<Func<FullProfile, bool>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeSearchResult(_requesterId, "Me") });

        var result = await _service.LookupRecipientsAsync("me", _requesterId);

        result.Should().BeEmpty();
    }

    // ============================================================================
    // CreateRequestAsync
    // ============================================================================

    [HumansFact]
    public async Task CreateRequestAsync_HappyPath_ReturnsPendingRow()
    {
        var attendee = MakeAttendee(_attendeeId, _orderId, _requesterId, TicketAttendeeStatus.Valid);
        _ticketRepo.GetAttendeeByIdAsync(_attendeeId, Arg.Any<CancellationToken>())
            .Returns(attendee);
        _userService.GetByIdAsync(_requesterId, Arg.Any<CancellationToken>())
            .Returns(MakeUser(_requesterId, "Bob"));

        var dto = new TicketTransferRequestDto(_attendeeId, _recipientId, "Going abroad");

        var row = await _service.CreateRequestAsync(dto, _requesterId);

        row.Status.Should().Be(TicketTransferStatus.Pending);
        row.RecipientUserId.Should().Be(_recipientId);
        row.RecipientDisplayName.Should().Be("Alice");
        await _transferRepo.Received(1).AddAsync(
            Arg.Is<TicketTransferRequest>(r => r.Status == TicketTransferStatus.Pending),
            Arg.Any<CancellationToken>());
        await _auditLog.Received(1).LogAsync(
            AuditAction.TicketTransferRequested,
            nameof(TicketTransferRequest),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            _requesterId,
            _recipientId,
            nameof(User));
    }

    [HumansFact]
    public async Task CreateRequestAsync_ThrowsWhenRecipientEqualsRequester()
    {
        var dto = new TicketTransferRequestDto(_attendeeId, _requesterId, "test");

        var act = () => _service.CreateRequestAsync(dto, _requesterId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*yourself*");
    }

    [HumansFact]
    public async Task CreateRequestAsync_ThrowsWhenAttendeeNotFound()
    {
        _ticketRepo.GetAttendeeByIdAsync(_attendeeId, Arg.Any<CancellationToken>())
            .Returns((TicketAttendee?)null);

        var dto = new TicketTransferRequestDto(_attendeeId, _recipientId, "test");

        var act = () => _service.CreateRequestAsync(dto, _requesterId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Attendee not found*");
    }

    [HumansFact]
    public async Task CreateRequestAsync_ThrowsWhenRequesterDoesNotOwnOrder()
    {
        var otherId = Guid.NewGuid();
        var attendee = MakeAttendee(_attendeeId, _orderId, otherId, TicketAttendeeStatus.Valid);
        _ticketRepo.GetAttendeeByIdAsync(_attendeeId, Arg.Any<CancellationToken>())
            .Returns(attendee);

        var dto = new TicketTransferRequestDto(_attendeeId, _recipientId, "test");

        var act = () => _service.CreateRequestAsync(dto, _requesterId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*your own orders*");
    }

    [HumansFact]
    public async Task CreateRequestAsync_ThrowsWhenAttendeeStatusNotValid()
    {
        var attendee = MakeAttendee(_attendeeId, _orderId, _requesterId, TicketAttendeeStatus.Void);
        _ticketRepo.GetAttendeeByIdAsync(_attendeeId, Arg.Any<CancellationToken>())
            .Returns(attendee);

        var dto = new TicketTransferRequestDto(_attendeeId, _recipientId, "test");

        var act = () => _service.CreateRequestAsync(dto, _requesterId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Valid tickets*");
    }

    [HumansFact]
    public async Task CreateRequestAsync_ThrowsWhenPendingTransferAlreadyExists()
    {
        var attendee = MakeAttendee(_attendeeId, _orderId, _requesterId, TicketAttendeeStatus.Valid);
        _ticketRepo.GetAttendeeByIdAsync(_attendeeId, Arg.Any<CancellationToken>())
            .Returns(attendee);
        _transferRepo.GetPendingForAttendeeAsync(_attendeeId, Arg.Any<CancellationToken>())
            .Returns(new TicketTransferRequest { Id = Guid.NewGuid() });

        var dto = new TicketTransferRequestDto(_attendeeId, _recipientId, "test");

        var act = () => _service.CreateRequestAsync(dto, _requesterId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*pending transfer already exists*");
    }

    [HumansFact]
    public async Task CreateRequestAsync_ThrowsWhenRecipientAlreadyHoldsValidTicket()
    {
        var attendee = MakeAttendee(_attendeeId, _orderId, _requesterId, TicketAttendeeStatus.Valid);
        _ticketRepo.GetAttendeeByIdAsync(_attendeeId, Arg.Any<CancellationToken>())
            .Returns(attendee);
        _ticketRepo.GetMatchedAttendeesForEventAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new MatchedAttendeeRow(_recipientId, TicketAttendeeStatus.Valid) });

        var dto = new TicketTransferRequestDto(_attendeeId, _recipientId, "test");

        var act = () => _service.CreateRequestAsync(dto, _requesterId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already holds a ticket*");
    }

    [HumansFact]
    public async Task CreateRequestAsync_ThrowsWhenRecipientAlreadyCheckedIn()
    {
        var attendee = MakeAttendee(_attendeeId, _orderId, _requesterId, TicketAttendeeStatus.Valid);
        _ticketRepo.GetAttendeeByIdAsync(_attendeeId, Arg.Any<CancellationToken>())
            .Returns(attendee);
        _ticketRepo.GetMatchedAttendeesForEventAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new MatchedAttendeeRow(_recipientId, TicketAttendeeStatus.CheckedIn) });

        var dto = new TicketTransferRequestDto(_attendeeId, _recipientId, "test");

        var act = () => _service.CreateRequestAsync(dto, _requesterId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already holds a ticket*");
    }

    [HumansFact]
    public async Task CreateRequestAsync_ThrowsWhenRecipientUserNotFound()
    {
        var attendee = MakeAttendee(_attendeeId, _orderId, _requesterId, TicketAttendeeStatus.Valid);
        _ticketRepo.GetAttendeeByIdAsync(_attendeeId, Arg.Any<CancellationToken>())
            .Returns(attendee);
        _userService.GetByIdAsync(_recipientId, Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var dto = new TicketTransferRequestDto(_attendeeId, _recipientId, "test");

        var act = () => _service.CreateRequestAsync(dto, _requesterId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Recipient user not found*");
    }

    [HumansFact]
    public async Task CreateRequestAsync_ThrowsWhenRecipientHasNoPrimaryEmail()
    {
        var attendee = MakeAttendee(_attendeeId, _orderId, _requesterId, TicketAttendeeStatus.Valid);
        _ticketRepo.GetAttendeeByIdAsync(_attendeeId, Arg.Any<CancellationToken>())
            .Returns(attendee);
        _userEmailService.GetPrimaryEmailAsync(_recipientId, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var dto = new TicketTransferRequestDto(_attendeeId, _recipientId, "test");

        var act = () => _service.CreateRequestAsync(dto, _requesterId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no primary email*");
    }

    // ============================================================================
    // CancelAsync
    // ============================================================================

    [HumansFact]
    public async Task CancelAsync_HappyPath_SetsCancelledAndAudit()
    {
        var transferId = Guid.NewGuid();
        var request = MakePendingRequest(transferId, _requesterId, _recipientId);
        _transferRepo.GetByIdAsync(transferId, Arg.Any<CancellationToken>())
            .Returns(request);

        await _service.CancelAsync(transferId, _requesterId);

        request.Status.Should().Be(TicketTransferStatus.Cancelled);
        request.DecidedAt.Should().Be(_now);
        await _transferRepo.Received(1).UpdateAsync(request, Arg.Any<CancellationToken>());
        await _auditLog.Received(1).LogAsync(
            AuditAction.TicketTransferCancelled,
            nameof(TicketTransferRequest),
            transferId,
            Arg.Any<string>(),
            _requesterId);
    }

    [HumansFact]
    public async Task CancelAsync_ThrowsWhenNotPending()
    {
        var transferId = Guid.NewGuid();
        var request = MakePendingRequest(transferId, _requesterId, _recipientId);
        request.Status = TicketTransferStatus.Approved;
        _transferRepo.GetByIdAsync(transferId, Arg.Any<CancellationToken>())
            .Returns(request);

        var act = () => _service.CancelAsync(transferId, _requesterId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Pending transfers can be cancelled*");
    }

    [HumansFact]
    public async Task CancelAsync_ThrowsWhenCallerIsNotRequester()
    {
        var transferId = Guid.NewGuid();
        var request = MakePendingRequest(transferId, _requesterId, _recipientId);
        _transferRepo.GetByIdAsync(transferId, Arg.Any<CancellationToken>())
            .Returns(request);

        var act = () => _service.CancelAsync(transferId, Guid.NewGuid() /* different caller */);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*requester can cancel*");
    }

    // ============================================================================
    // RejectAsync
    // ============================================================================

    [HumansFact]
    public async Task RejectAsync_HappyPath_SetsRejectedFieldsAndAudit()
    {
        var transferId = Guid.NewGuid();
        var request = MakePendingRequest(transferId, _requesterId, _recipientId);
        _transferRepo.GetByIdAsync(transferId, Arg.Any<CancellationToken>())
            .Returns(request);
        WireRequesterAndAttendeeForRow(request);

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
            _requesterId,
            nameof(User));
    }

    [HumansFact]
    public async Task RejectAsync_ThrowsWhenNotPending()
    {
        var transferId = Guid.NewGuid();
        var request = MakePendingRequest(transferId, _requesterId, _recipientId);
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
    public async Task ApproveAsync_HappyPath_VendorSucceeds_SetsSucceededAndUpsertsTwice()
    {
        var transferId = Guid.NewGuid();
        var request = MakePendingRequest(transferId, _requesterId, _recipientId);
        _transferRepo.GetByIdAsync(transferId, Arg.Any<CancellationToken>())
            .Returns(request);

        var attendee = MakeAttendee(_attendeeId, _orderId, _requesterId, TicketAttendeeStatus.Valid);
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

        WireRequesterAndAttendeeForRow(request);

        var row = await _service.ApproveAsync(transferId, _adminId, null);

        row.Status.Should().Be(TicketTransferStatus.Approved);
        request.VendorResult.Should().Be(TicketTransferVendorResult.Succeeded);
        request.NewVendorTicketId.Should().Be("tkt_new");
        await _ticketRepo.Received(2).UpsertAttendeeAsync(Arg.Any<TicketAttendee>(), Arg.Any<CancellationToken>());
        await _auditLog.Received(1).LogAsync(
            AuditAction.TicketTransferApproved,
            nameof(TicketTransferRequest),
            transferId,
            Arg.Is<string>(s => s.Contains("TT void+reissue OK")),
            _adminId,
            _requesterId,
            nameof(User));
    }

    // ============================================================================
    // ApproveAsync — vendor branch: void succeeds, issue throws
    // ============================================================================

    [HumansFact]
    public async Task ApproveAsync_VoidOk_IssueFails_SetsVoidSucceededIssueFailed()
    {
        var transferId = Guid.NewGuid();
        var request = MakePendingRequest(transferId, _requesterId, _recipientId);
        _transferRepo.GetByIdAsync(transferId, Arg.Any<CancellationToken>())
            .Returns(request);

        var attendee = MakeAttendee(_attendeeId, _orderId, _requesterId, TicketAttendeeStatus.Valid);
        request.OriginalTicketAttendee = attendee;

        _vendor.VoidIssuedTicketAsync("tkt_original", true, Arg.Any<CancellationToken>())
            .Returns(new VoidIssuedTicketResult("tkt_original", "hold_002"));
        _vendor.IssueTicketAsync(Arg.Any<IssueTicketRequest>(), Arg.Any<CancellationToken>())
            .Throws(new TicketVendorWriteException("Sold out", TicketVendorFailureKind.Validation));

        WireRequesterAndAttendeeForRow(request);

        var row = await _service.ApproveAsync(transferId, _adminId, null);

        row.Status.Should().Be(TicketTransferStatus.Approved);
        request.VendorResult.Should().Be(TicketTransferVendorResult.VoidSucceededIssueFailed);
        request.VendorMessage.Should().StartWith("Issue failed");
        await _auditLog.Received(1).LogAsync(
            AuditAction.TicketTransferApproved,
            nameof(TicketTransferRequest),
            transferId,
            Arg.Is<string>(s => s.Contains("manual reissue needed")),
            _adminId,
            _requesterId,
            nameof(User));
    }

    // ============================================================================
    // ApproveAsync — vendor branch: void throws
    // ============================================================================

    [HumansFact]
    public async Task ApproveAsync_VoidFails_SetsFailed_OptionCFallback()
    {
        var transferId = Guid.NewGuid();
        var request = MakePendingRequest(transferId, _requesterId, _recipientId);
        _transferRepo.GetByIdAsync(transferId, Arg.Any<CancellationToken>())
            .Returns(request);

        var attendee = MakeAttendee(_attendeeId, _orderId, _requesterId, TicketAttendeeStatus.Valid);
        request.OriginalTicketAttendee = attendee;

        _vendor.VoidIssuedTicketAsync("tkt_original", true, Arg.Any<CancellationToken>())
            .Throws(new TicketVendorWriteException("TT 500", TicketVendorFailureKind.Transient));

        WireRequesterAndAttendeeForRow(request);

        var row = await _service.ApproveAsync(transferId, _adminId, null);

        row.Status.Should().Be(TicketTransferStatus.Approved);
        request.VendorResult.Should().Be(TicketTransferVendorResult.Failed);
        request.VendorMessage.Should().StartWith("Void failed");
        await _ticketRepo.DidNotReceive().UpsertAttendeeAsync(Arg.Any<TicketAttendee>(), Arg.Any<CancellationToken>());
        await _auditLog.Received(1).LogAsync(
            AuditAction.TicketTransferApproved,
            nameof(TicketTransferRequest),
            transferId,
            Arg.Is<string>(s => s.Contains("Option-C fallback")),
            _adminId,
            _requesterId,
            nameof(User));
    }

    [HumansFact]
    public async Task ApproveAsync_ThrowsWhenNotPending()
    {
        var transferId = Guid.NewGuid();
        var request = MakePendingRequest(transferId, _requesterId, _recipientId);
        request.Status = TicketTransferStatus.Cancelled;
        _transferRepo.GetByIdAsync(transferId, Arg.Any<CancellationToken>())
            .Returns(request);

        var act = () => _service.ApproveAsync(transferId, _adminId, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Pending transfers can be decided*");
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

    private static TicketTransferRequest MakePendingRequest(Guid id, Guid requesterId, Guid recipientId) =>
        new()
        {
            Id = id,
            OriginalTicketAttendeeId = _attendeeId,
            RequesterUserId = requesterId,
            RecipientUserId = recipientId,
            RecipientDisplayName = "Alice",
            RecipientEmail = "alice@example.com",
            RequesterReason = "Going abroad",
            Status = TicketTransferStatus.Pending,
            VendorResult = TicketTransferVendorResult.NotAttempted,
            RequestedAt = _now,
        };

    private static HumanSearchResult MakeSearchResult(Guid userId, string displayName) =>
        new(
            UserId: userId,
            DisplayName: displayName,
            BurnerName: null,
            City: null,
            Bio: null,
            ContributionInterests: null,
            ProfilePictureUrl: null,
            HasCustomPicture: false,
            ProfileId: Guid.NewGuid(),
            UpdatedAtTicks: 0L,
            MatchField: "Burner Name",
            MatchSnippet: null);

    /// <summary>
    /// Wires up GetByIdAsync for requester and attendee so BuildRowDtoAsync
    /// (called at the end of create/reject/approve) can complete without null-ref.
    /// </summary>
    private void WireRequesterAndAttendeeForRow(TicketTransferRequest request)
    {
        _userService.GetByIdAsync(request.RequesterUserId, Arg.Any<CancellationToken>())
            .Returns(MakeUser(request.RequesterUserId, "Bob"));

        if (request.OriginalTicketAttendee is null)
        {
            var attendee = MakeAttendee(
                request.OriginalTicketAttendeeId,
                _orderId,
                request.RequesterUserId,
                TicketAttendeeStatus.Valid);
            _ticketRepo.GetAttendeeByIdAsync(request.OriginalTicketAttendeeId, Arg.Any<CancellationToken>())
                .Returns(attendee);
        }
    }
}

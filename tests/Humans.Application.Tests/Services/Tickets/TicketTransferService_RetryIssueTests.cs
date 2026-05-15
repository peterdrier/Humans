using System.Text.Json;
using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Tickets;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Humans.Application.Tests.Services.Tickets;

public sealed class TicketTransferService_RetryIssueTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 5, 12, 10, 0);
    private readonly FakeClock _clock = new(Now);
    private static readonly Guid AdminId = Guid.NewGuid();
    private static readonly Guid SenderId = Guid.NewGuid();
    private static readonly Guid ReceiverId = Guid.NewGuid();

    private readonly ITicketTransferRepository _transferRepo = Substitute.For<ITicketTransferRepository>();
    private readonly ITicketRepository _ticketRepo = Substitute.For<ITicketRepository>();
    private readonly ITicketVendorService _vendor = Substitute.For<ITicketVendorService>();
    private readonly ITicketQueryService _ticketQueryService = Substitute.For<ITicketQueryService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IProfileService _profileService = Substitute.For<IProfileService>();
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();
    private readonly TicketTransferService _service;

    public TicketTransferService_RetryIssueTests()
    {
        _service = new TicketTransferService(_transferRepo, _ticketRepo, _vendor,
            _ticketQueryService, _userService, _userEmailService, _profileService,
            _auditLog, _clock, NullLogger<TicketTransferService>.Instance);
        var sender = new User { Id = SenderId, DisplayName = "Sender" };
        _userService.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(sender);
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ids = callInfo.Arg<IReadOnlyCollection<Guid>>();
                IReadOnlyDictionary<Guid, UserInfo> dict = ids.ToDictionary(
                    id => id,
                    id => (id == SenderId ? sender : new User { Id = id, DisplayName = id.ToString() }).ToUserInfo());
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(dict);
            });
    }

    private static TicketTransferRequest PendingRequest(Guid attendeeId) => new()
    {
        Id = Guid.NewGuid(),
        OriginalTicketAttendeeId = attendeeId,
        SenderUserId = SenderId,
        ReceiverUserId = ReceiverId,
        ReceiverLegalName = "Alice",
        ReceiverEmail = "alice@example.com",
        Status = TicketTransferStatus.Pending,
        VendorResult = TicketTransferVendorResult.NotAttempted,
        RequestedAt = Now,
        VendorStepsJson = "[]",
    };

    private static TicketAttendee ValidAttendee(Guid id) => new()
    {
        Id = id,
        VendorTicketId = "tt_orig",
        Status = TicketAttendeeStatus.Valid,
        VendorEventId = "ev_1",
        AttendeeName = "Original Holder",
        AttendeeEmail = "orig@example.com",
        TicketTypeName = "Standard",
        Price = 50m,
        TicketOrder = new TicketOrder { Id = Guid.NewGuid(), MatchedUserId = SenderId },
        TicketOrderId = Guid.NewGuid(),
    };

    private static readonly JsonSerializerOptions WebOptions =
        new JsonSerializerOptions(JsonSerializerDefaults.Web)
            .ConfigureForNodaTime(NodaTime.DateTimeZoneProviders.Tzdb);

    private static IReadOnlyList<TicketTransferVendorStep> StepsOf(TicketTransferRequest r) =>
        JsonSerializer.Deserialize<List<TicketTransferVendorStep>>(r.VendorStepsJson, WebOptions)!;

    /// <summary>
    /// Drive ApproveAsync through a void-ok/issue-fail scenario so the resulting request
    /// has properly-serialized VendorStepsJson (Instant encoded by the real AppendStep logic).
    /// Returns the request in Approved + VoidSucceededIssueFailed state, ready for RetryIssue tests.
    /// </summary>
    private async Task<TicketTransferRequest> BuildPartiallyFailedRequestAsync(Guid attendeeId)
    {
        var request = PendingRequest(attendeeId);
        _transferRepo.GetByIdAsync(request.Id, Arg.Any<CancellationToken>()).Returns(request);
        _ticketRepo.GetAttendeeByIdAsync(attendeeId, Arg.Any<CancellationToken>())
            .Returns(ValidAttendee(attendeeId));
        _vendor.VoidIssuedTicketAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new VoidIssuedTicketResult("tt_orig", "hold_abc"));
        _vendor.IssueTicketAsync(Arg.Any<IssueTicketRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TicketVendorWriteException("issue failed", TicketVendorFailureKind.Validation));

        await _service.ApproveAsync(request.Id, AdminId, null);

        // After approve, request is Approved + VoidSucceededIssueFailed.
        // Clear the IssueTicketAsync stub so tests can re-configure it freely.
        _vendor.IssueTicketAsync(Arg.Any<IssueTicketRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("IssueTicketAsync not stubbed for retry"));

        return request;
    }

    [HumansFact]
    public async Task RetryIssue_RejectsRequest_NotInPartialFailureState()
    {
        var request = PendingRequest(Guid.NewGuid()); // Status=Pending
        _transferRepo.GetByIdAsync(request.Id, Arg.Any<CancellationToken>()).Returns(request);

        var act = async () => await _service.RetryIssueAsync(request.Id, AdminId, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*VoidSucceededIssueFailed*");
    }

    [HumansFact]
    public async Task RetryIssue_RejectsRequest_WithNoRecordedHoldId()
    {
        var request = new TicketTransferRequest
        {
            Id = Guid.NewGuid(),
            OriginalTicketAttendeeId = Guid.NewGuid(),
            SenderUserId = SenderId,
            ReceiverUserId = ReceiverId,
            ReceiverLegalName = "Alice",
            ReceiverEmail = "alice@example.com",
            Status = TicketTransferStatus.Approved,
            VendorResult = TicketTransferVendorResult.VoidSucceededIssueFailed,
            VendorStepsJson = "[]",
            RequestedAt = Now,
        };
        _transferRepo.GetByIdAsync(request.Id, Arg.Any<CancellationToken>()).Returns(request);

        var act = async () => await _service.RetryIssueAsync(request.Id, AdminId, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*hold*");
    }

    [HumansFact]
    public async Task RetryIssue_HappyPath_InsertsAttendeeFlipsResultAppendsStep()
    {
        var attendeeId = Guid.NewGuid();
        var request = await BuildPartiallyFailedRequestAsync(attendeeId);

        // Re-configure for the retry: attendee is now Void (after the partial failure),
        // and vendor succeeds this time.
        var voidedAttendee = ValidAttendee(attendeeId);
        voidedAttendee.Status = TicketAttendeeStatus.Void;
        _ticketRepo.GetAttendeeByIdAsync(attendeeId, Arg.Any<CancellationToken>())
            .Returns(voidedAttendee);
        _vendor.IssueTicketAsync(Arg.Any<IssueTicketRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VendorTicketDto("tt_new2", null, "Alice", "alice@example.com", "Standard", 50m, "valid"));

        var row = await _service.RetryIssueAsync(request.Id, AdminId, "retrying");

        request.VendorResult.Should().Be(TicketTransferVendorResult.Succeeded);
        request.NewVendorTicketId.Should().Be("tt_new2");
        var steps = StepsOf(request);
        steps.Last().Kind.Should().Be(TicketTransferVendorStepKind.RetryIssue);
        steps.Last().Success.Should().BeTrue();
        await _ticketRepo.Received(1).UpsertAttendeeAsync(
            Arg.Is<TicketAttendee>(a => a.VendorTicketId == "tt_new2" && a.MatchedUserId == ReceiverId),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RetryIssue_VendorFailure_AppendsFailedStepAndLeavesStateUnchanged()
    {
        var attendeeId = Guid.NewGuid();
        var request = await BuildPartiallyFailedRequestAsync(attendeeId);

        var voidedAttendee = ValidAttendee(attendeeId);
        voidedAttendee.Status = TicketAttendeeStatus.Void;
        _ticketRepo.GetAttendeeByIdAsync(attendeeId, Arg.Any<CancellationToken>())
            .Returns(voidedAttendee);
        _vendor.IssueTicketAsync(Arg.Any<IssueTicketRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TicketVendorWriteException("quota exceeded", TicketVendorFailureKind.RateLimited));

        // Reset call history so the UpsertAttendeeAsync call from ApproveAsync setup doesn't
        // interfere with the DidNotReceive assertion below.
        _ticketRepo.ClearReceivedCalls();

        await _service.RetryIssueAsync(request.Id, AdminId, null);

        request.VendorResult.Should().Be(TicketTransferVendorResult.VoidSucceededIssueFailed);
        var steps = StepsOf(request);
        steps.Last().Kind.Should().Be(TicketTransferVendorStepKind.RetryIssue);
        steps.Last().Success.Should().BeFalse();
        await _ticketRepo.DidNotReceive().UpsertAttendeeAsync(
            Arg.Any<TicketAttendee>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RetryIssue_VendorSuccess_LocalFailure_AuditsAndRethrows()
    {
        var attendeeId = Guid.NewGuid();
        var request = await BuildPartiallyFailedRequestAsync(attendeeId);

        var voidedAttendee = ValidAttendee(attendeeId);
        voidedAttendee.Status = TicketAttendeeStatus.Void;
        _ticketRepo.GetAttendeeByIdAsync(attendeeId, Arg.Any<CancellationToken>())
            .Returns(voidedAttendee);
        _vendor.IssueTicketAsync(Arg.Any<IssueTicketRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VendorTicketDto("tt_new3", null, "Alice", "alice@example.com", "Standard", 50m, "valid"));

        // Simulate a DB failure on the local writeback.
        var dbError = new InvalidOperationException("simulated db error");
        _ticketRepo.UpsertAttendeeAsync(Arg.Any<TicketAttendee>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(dbError);

        var act = async () => await _service.RetryIssueAsync(request.Id, AdminId, null);

        // Exception bubbles out.
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated db error");

        // Audit log received a PARTIAL STATE call.
        await _auditLog.Received(1).LogAsync(
            AuditAction.TicketTransferApproved,
            nameof(TicketTransferRequest),
            request.Id,
            Arg.Is<string>(s => s.Contains("PARTIAL STATE")),
            AdminId,
            SenderId,
            nameof(User));
    }
}

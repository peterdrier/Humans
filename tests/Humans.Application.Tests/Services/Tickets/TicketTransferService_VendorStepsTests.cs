using System.Text.Json;
using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Tickets;
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

public sealed class TicketTransferService_VendorStepsTests
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

    public TicketTransferService_VendorStepsTests()
    {
        _service = new TicketTransferService(_transferRepo, _ticketRepo, _vendor,
            _ticketQueryService, _userService, _userEmailService, _profileService,
            _auditLog, _clock, NullLogger<TicketTransferService>.Instance);
        _userService.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new User { Id = SenderId, DisplayName = "Sender" });
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

    [HumansFact]
    public async Task Approve_HappyPath_AppendsVoidIssueAndLocalWritebackSteps()
    {
        var attendeeId = Guid.NewGuid();
        var request = PendingRequest(attendeeId);
        _transferRepo.GetByIdAsync(request.Id, Arg.Any<CancellationToken>()).Returns(request);
        _ticketRepo.GetAttendeeByIdAsync(attendeeId, Arg.Any<CancellationToken>()).Returns(ValidAttendee(attendeeId));
        _vendor.VoidIssuedTicketAsync("tt_orig", true, Arg.Any<CancellationToken>())
            .Returns(new VoidIssuedTicketResult("tt_orig", "hold_123"));
        _vendor.IssueTicketAsync(Arg.Any<IssueTicketRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VendorTicketDto("tt_new", null, "Alice", "alice@example.com", "Standard", 50m, "valid"));

        await _service.ApproveAsync(request.Id, AdminId, null);

        var steps = StepsOf(request);
        steps.Should().HaveCount(3);
        steps[0].Kind.Should().Be(TicketTransferVendorStepKind.Void);
        steps[0].Success.Should().BeTrue();
        steps[0].VendorReferenceId.Should().Be("hold_123");
        steps[1].Kind.Should().Be(TicketTransferVendorStepKind.Issue);
        steps[1].Success.Should().BeTrue();
        steps[1].VendorReferenceId.Should().Be("tt_new");
        steps[2].Kind.Should().Be(TicketTransferVendorStepKind.LocalWriteback);
        steps[2].Success.Should().BeTrue();
        steps[0].OccurredAt.Should().Be(Now);
    }

    [HumansFact]
    public async Task Approve_VoidFails_AppendsOnlyFailedVoidStep()
    {
        var attendeeId = Guid.NewGuid();
        var request = PendingRequest(attendeeId);
        _transferRepo.GetByIdAsync(request.Id, Arg.Any<CancellationToken>()).Returns(request);
        _ticketRepo.GetAttendeeByIdAsync(attendeeId, Arg.Any<CancellationToken>()).Returns(ValidAttendee(attendeeId));
        _vendor.VoidIssuedTicketAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TicketVendorWriteException("denied", TicketVendorFailureKind.Validation));

        await _service.ApproveAsync(request.Id, AdminId, null);

        var steps = StepsOf(request);
        steps.Should().HaveCount(1);
        steps[0].Kind.Should().Be(TicketTransferVendorStepKind.Void);
        steps[0].Success.Should().BeFalse();
        steps[0].ErrorMessage.Should().Contain("denied");
        request.VendorResult.Should().Be(TicketTransferVendorResult.Failed);
    }

    [HumansFact]
    public async Task Approve_IssueFails_AppendsSuccessfulVoidAndFailedIssue()
    {
        var attendeeId = Guid.NewGuid();
        var request = PendingRequest(attendeeId);
        _transferRepo.GetByIdAsync(request.Id, Arg.Any<CancellationToken>()).Returns(request);
        _ticketRepo.GetAttendeeByIdAsync(attendeeId, Arg.Any<CancellationToken>()).Returns(ValidAttendee(attendeeId));
        _vendor.VoidIssuedTicketAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new VoidIssuedTicketResult("tt_orig", "hold_abc"));
        _vendor.IssueTicketAsync(Arg.Any<IssueTicketRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TicketVendorWriteException("rate limited", TicketVendorFailureKind.RateLimited));

        await _service.ApproveAsync(request.Id, AdminId, null);

        var steps = StepsOf(request);
        steps.Should().HaveCount(2);
        steps[0].Kind.Should().Be(TicketTransferVendorStepKind.Void);
        steps[0].Success.Should().BeTrue();
        steps[0].VendorReferenceId.Should().Be("hold_abc");
        steps[1].Kind.Should().Be(TicketTransferVendorStepKind.Issue);
        steps[1].Success.Should().BeFalse();
        request.VendorResult.Should().Be(TicketTransferVendorResult.VoidSucceededIssueFailed);
    }
}

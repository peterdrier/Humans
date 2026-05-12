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
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Testing;

namespace Humans.Application.Tests.Services.Tickets;

public sealed class TicketTransferService_OnwardTransferTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 5, 12, 10, 0);
    private readonly FakeClock _clock = new(Now);

    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();
    private static readonly Guid UserC = Guid.NewGuid();
    private static readonly Guid OrderId = Guid.NewGuid();

    private readonly ITicketTransferRepository _transferRepo = Substitute.For<ITicketTransferRepository>();
    private readonly ITicketRepository _ticketRepo = Substitute.For<ITicketRepository>();
    private readonly ITicketVendorService _vendor = Substitute.For<ITicketVendorService>();
    private readonly ITicketQueryService _ticketQueryService = Substitute.For<ITicketQueryService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IProfileService _profileService = Substitute.For<IProfileService>();
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();

    private readonly TicketTransferService _service;

    public TicketTransferService_OnwardTransferTests()
    {
        _service = new TicketTransferService(_transferRepo, _ticketRepo, _vendor,
            _ticketQueryService, _userService, _userEmailService, _profileService,
            _auditLog, _clock, NullLogger<TicketTransferService>.Instance);
    }

    [HumansFact]
    public async Task GetMyAttendees_AllowsTransfer_WhenAttendeeMatchedToCaller_EvenIfBuyerIsSomeoneElse()
    {
        var attendeeId = Guid.NewGuid();
        var attendee = new TicketAttendee
        {
            Id = attendeeId,
            Status = TicketAttendeeStatus.Valid,
            MatchedUserId = UserB,
            TicketOrder = new TicketOrder { Id = OrderId, MatchedUserId = UserA },
        };
        _ticketRepo.GetAttendeesVisibleToUserAsync(UserB, Arg.Any<CancellationToken>())
            .Returns(new[] { attendee });
        _transferRepo.GetBySenderAsync(UserB, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TicketTransferRequest>());

        var rows = await _service.GetMyAttendeesAsync(UserB);

        rows.Should().HaveCount(1);
        rows[0].CanSendTransfer.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetMyAttendees_DeniesTransfer_WhenAttendeeMatchedToSomeoneElse_EvenIfCallerIsBuyer()
    {
        var attendee = new TicketAttendee
        {
            Id = Guid.NewGuid(),
            Status = TicketAttendeeStatus.Valid,
            MatchedUserId = UserB,
            TicketOrder = new TicketOrder { Id = OrderId, MatchedUserId = UserA },
        };
        _ticketRepo.GetAttendeesVisibleToUserAsync(UserA, Arg.Any<CancellationToken>())
            .Returns(new[] { attendee });
        _transferRepo.GetBySenderAsync(UserA, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TicketTransferRequest>());

        var rows = await _service.GetMyAttendeesAsync(UserA);

        rows.Should().HaveCount(1);
        rows[0].CanSendTransfer.Should().BeFalse();
    }

    [HumansFact]
    public async Task GetMyAttendees_AllowsBuyer_WhenAttendeeUnmatched()
    {
        var attendee = new TicketAttendee
        {
            Id = Guid.NewGuid(),
            Status = TicketAttendeeStatus.Valid,
            MatchedUserId = null,
            TicketOrder = new TicketOrder { Id = OrderId, MatchedUserId = UserA },
        };
        _ticketRepo.GetAttendeesVisibleToUserAsync(UserA, Arg.Any<CancellationToken>())
            .Returns(new[] { attendee });
        _transferRepo.GetBySenderAsync(UserA, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TicketTransferRequest>());

        var rows = await _service.GetMyAttendeesAsync(UserA);

        rows[0].CanSendTransfer.Should().BeTrue();
    }

    [HumansFact]
    public async Task CreateRequest_RejectsBuyer_WhenAttendeeMatchedToDifferentUser()
    {
        var attendeeId = Guid.NewGuid();
        _ticketRepo.GetAttendeeByIdAsync(attendeeId, Arg.Any<CancellationToken>())
            .Returns(new TicketAttendee
            {
                Id = attendeeId,
                Status = TicketAttendeeStatus.Valid,
                MatchedUserId = UserB,
                TicketOrder = new TicketOrder { Id = OrderId, MatchedUserId = UserA },
            });

        var act = async () => await _service.CreateRequestAsync(
            new TicketTransferRequestDto(attendeeId, UserC, "test"), UserA);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*currently hold*");
    }

    [HumansFact]
    public async Task CreateRequest_AllowsAttendeeHolder_EvenIfBuyerIsSomeoneElse()
    {
        var attendeeId = Guid.NewGuid();
        _ticketRepo.GetAttendeeByIdAsync(attendeeId, Arg.Any<CancellationToken>())
            .Returns(new TicketAttendee
            {
                Id = attendeeId,
                Status = TicketAttendeeStatus.Valid,
                MatchedUserId = UserB,
                TicketOrder = new TicketOrder { Id = OrderId, MatchedUserId = UserA },
            });
        _userService.GetByIdAsync(UserC, Arg.Any<CancellationToken>())
            .Returns(new User { Id = UserC, DisplayName = "Carol" });
        _userEmailService.GetPrimaryEmailAsync(UserC, Arg.Any<CancellationToken>())
            .Returns("carol@example.com");
        _transferRepo.GetBySenderAsync(UserB, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TicketTransferRequest>());

        var result = await _service.CreateRequestAsync(
            new TicketTransferRequestDto(attendeeId, UserC, "passing to Carol"), UserB);

        result.SenderUserId.Should().Be(UserB);
        result.ReceiverUserId.Should().Be(UserC);
    }
}

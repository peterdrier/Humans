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
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();

    private readonly TicketTransferService _service;

    public TicketTransferService_OnwardTransferTests()
    {
        _service = new TicketTransferService(_transferRepo, _ticketRepo,
            _userService, _userEmailService, _emailService,
            _auditLog, _clock, NullLogger<TicketTransferService>.Instance);

        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ids = callInfo.Arg<IReadOnlyCollection<Guid>>();
                IReadOnlyDictionary<Guid, UserInfo> dict = ids.ToDictionary(
                    id => id,
                    id => new User { Id = id, DisplayName = id.ToString() }.ToUserInfo());
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(dict);
            });
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
            .Returns([attendee]);
        _transferRepo.GetBySenderAsync(UserB, Arg.Any<CancellationToken>())
            .Returns([]);

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
            .Returns([attendee]);
        _transferRepo.GetBySenderAsync(UserA, Arg.Any<CancellationToken>())
            .Returns([]);

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
            .Returns([attendee]);
        _transferRepo.GetBySenderAsync(UserA, Arg.Any<CancellationToken>())
            .Returns([]);

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
        var carol = new User { Id = UserC, DisplayName = "Carol", PreferredLanguage = "en" };
        var carolProfile = new Profile { BurnerName = "Carol", FirstName = "Carol", LastName = "Cohen" };
        _userService.GetUserInfoAsync(UserC, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(
                user: carol,
                userEmails: [],
                eventParticipations: [],
                externalLogins: [],
                profile: carolProfile,
                contactFields: [],
                profileLanguages: [],
                volunteerHistory: [],
                communicationPreferences: []));
        _userEmailService.GetPrimaryEmailAsync(UserC, Arg.Any<CancellationToken>())
            .Returns("carol@example.com");
        _transferRepo.GetBySenderAsync(UserB, Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await _service.CreateRequestAsync(
            new TicketTransferRequestDto(attendeeId, UserC, "passing to Carol"), UserB);

        result.SenderUserId.Should().Be(UserB);
        result.ReceiverUserId.Should().Be(UserC);
    }
}

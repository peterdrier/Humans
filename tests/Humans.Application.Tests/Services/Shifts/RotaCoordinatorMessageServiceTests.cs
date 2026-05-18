using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Shifts;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Shifts;

/// <summary>
/// Orchestration tests for <see cref="RotaCoordinatorMessageService"/>:
/// recipient fan-out, per-recipient shift list, audit log, and the failure shapes
/// (missing message body, missing rota, missing signups). Issue
/// nobodies-collective/Humans#732.
/// </summary>
public sealed class RotaCoordinatorMessageServiceTests
{
    private readonly IShiftSignupRepository _signupRepo = Substitute.For<IShiftSignupRepository>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();

    private RotaCoordinatorMessageService CreateSut() =>
        new(_signupRepo, _userService, _emailService, _auditLog,
            NullLogger<RotaCoordinatorMessageService>.Instance);

    [HumansFact]
    public async Task SendRotaMessageAsync_RejectsBlankMessage()
    {
        var result = await CreateSut().SendRotaMessageAsync(Guid.NewGuid(), Guid.NewGuid(), "   ");

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("required", "blank body must surface a clear error");
        await _emailService.DidNotReceiveWithAnyArgs().SendCoordinatorRotaMessageAsync(null!);
        await _auditLog.DidNotReceiveWithAnyArgs().LogAsync(
            default, null!, Guid.Empty, null!, Guid.Empty);
    }

    [HumansFact]
    public async Task SendRotaMessageAsync_ReturnsFailure_WhenRotaMissing()
    {
        var rotaId = Guid.NewGuid();
        _signupRepo.GetRotaWithShiftsAsync(rotaId, Arg.Any<CancellationToken>())
            .Returns((Rota?)null);

        var result = await CreateSut().SendRotaMessageAsync(rotaId, Guid.NewGuid(), "hello");

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("not found");
        await _emailService.DidNotReceiveWithAnyArgs().SendCoordinatorRotaMessageAsync(null!);
    }

    [HumansFact]
    public async Task SendRotaMessageAsync_ReturnsFailure_WhenNoActiveSignups()
    {
        var rota = MakeRota(out var eventSettings);
        _signupRepo.GetRotaWithShiftsAsync(rota.Id, Arg.Any<CancellationToken>()).Returns(rota);
        _signupRepo.GetActiveByRotaAsync(rota.Id, Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await CreateSut().SendRotaMessageAsync(rota.Id, Guid.NewGuid(), "hello");

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("no active signups");
    }

    [HumansFact]
    public async Task SendRotaMessageAsync_FansOut_OneEmailPerDistinctUser()
    {
        var rota = MakeRota(out var es);
        _signupRepo.GetRotaWithShiftsAsync(rota.Id, Arg.Any<CancellationToken>()).Returns(rota);

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var sender = Guid.NewGuid();

        var shift1 = MakeShift(rota.Id, dayOffset: 1, startHour: 10);
        var shift2 = MakeShift(rota.Id, dayOffset: 2, startHour: 14);

        // userA has two signups (same shift twice + another shift), userB has one.
        _signupRepo.GetActiveByRotaAsync(rota.Id, Arg.Any<CancellationToken>())
            .Returns([
                MakeSignup(userA, shift1),
                MakeSignup(userA, shift2),
                MakeSignup(userB, shift1)
            ]);

        StubUsers(sender, userA, userB);

        await CreateSut().SendRotaMessageAsync(rota.Id, sender, "hello team");

        await _emailService.Received(1).SendCoordinatorRotaMessageAsync(
            Arg.Is<CoordinatorRotaMessageRequest>(r => r.RecipientEmail == "a@example.com"),
            Arg.Any<CancellationToken>());
        await _emailService.Received(1).SendCoordinatorRotaMessageAsync(
            Arg.Is<CoordinatorRotaMessageRequest>(r => r.RecipientEmail == "b@example.com"),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendRotaMessageAsync_PersonalisesShiftListPerRecipient()
    {
        var rota = MakeRota(out var es);
        _signupRepo.GetRotaWithShiftsAsync(rota.Id, Arg.Any<CancellationToken>()).Returns(rota);

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var sender = Guid.NewGuid();

        var earlyShift = MakeShift(rota.Id, dayOffset: 1, startHour: 9);
        var lateShift = MakeShift(rota.Id, dayOffset: 3, startHour: 18);
        var midShift = MakeShift(rota.Id, dayOffset: 2, startHour: 12);

        _signupRepo.GetActiveByRotaAsync(rota.Id, Arg.Any<CancellationToken>())
            .Returns([
                MakeSignup(userA, lateShift),  // userA: late + early (should sort)
                MakeSignup(userA, earlyShift),
                MakeSignup(userB, midShift) // userB: only midShift
            ]);

        StubUsers(sender, userA, userB);

        CoordinatorRotaMessageRequest? captured_A = null;
        CoordinatorRotaMessageRequest? captured_B = null;
        await _emailService.SendCoordinatorRotaMessageAsync(
            Arg.Do<CoordinatorRotaMessageRequest>(r =>
            {
                if (string.Equals(r.RecipientEmail, "a@example.com", StringComparison.Ordinal)) captured_A = r;
                else if (string.Equals(r.RecipientEmail, "b@example.com", StringComparison.Ordinal)) captured_B = r;
            }),
            Arg.Any<CancellationToken>());

        await CreateSut().SendRotaMessageAsync(rota.Id, sender, "hello");

        captured_A.Should().NotBeNull();
        captured_A!.ShiftLines.Should().HaveCount(2, "userA has 2 distinct shifts on this rota");
        captured_A.ShiftLines[0].Should().Contain("09:00", "earlier shift must come first");
        captured_A.ShiftLines[1].Should().Contain("18:00");

        captured_B.Should().NotBeNull();
        captured_B!.ShiftLines.Should().ContainSingle("userB has only one shift on this rota");
        captured_B.ShiftLines[0].Should().Contain("12:00");
    }

    [HumansFact]
    public async Task SendRotaMessageAsync_WritesOneAuditEntry_WithSenderActor()
    {
        var rota = MakeRota(out _);
        _signupRepo.GetRotaWithShiftsAsync(rota.Id, Arg.Any<CancellationToken>()).Returns(rota);

        var userA = Guid.NewGuid();
        var sender = Guid.NewGuid();
        var shift = MakeShift(rota.Id, dayOffset: 1, startHour: 10);

        _signupRepo.GetActiveByRotaAsync(rota.Id, Arg.Any<CancellationToken>())
            .Returns([MakeSignup(userA, shift)]);

        StubUsers(sender, userA);

        var result = await CreateSut().SendRotaMessageAsync(rota.Id, sender, "schedule change");

        result.Succeeded.Should().BeTrue();
        result.RecipientCount.Should().Be(1);
        result.RotaName.Should().Be(rota.Name);

        await _auditLog.Received(1).LogAsync(
            AuditAction.CoordinatorRotaMessageSent,
            nameof(Rota),
            rota.Id,
            Arg.Is<string>(d => d.Contains("schedule change") && d.Contains(rota.Name)),
            sender,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    [HumansFact]
    public async Task SendRotaMessageAsync_SkipsRecipientWithNoEmail_DoesNotFail()
    {
        var rota = MakeRota(out _);
        _signupRepo.GetRotaWithShiftsAsync(rota.Id, Arg.Any<CancellationToken>()).Returns(rota);

        var withEmail = Guid.NewGuid();
        var noEmail = Guid.NewGuid();
        var sender = Guid.NewGuid();
        var shift = MakeShift(rota.Id, dayOffset: 1, startHour: 10);

        _signupRepo.GetActiveByRotaAsync(rota.Id, Arg.Any<CancellationToken>())
            .Returns([
                MakeSignup(withEmail, shift),
                MakeSignup(noEmail, shift)
            ]);

        // sender + withEmail have addresses; noEmail's UserInfo has an empty email.
        var dict = new Dictionary<Guid, UserInfo>
        {
            [sender] = UserInfoStubHelpers.MakeUserInfo(sender, displayName: "Sender"),
            [withEmail] = MakeUserInfoWithEmail(withEmail, "with@example.com", "WithEmail"),
            [noEmail] = UserInfoStubHelpers.MakeUserInfo(noEmail, displayName: "NoEmail"),
        };
        // Need to make sender resolvable too.
        _userService.GetUserInfosAsync(Arg.Is<IReadOnlyCollection<Guid>>(c => c.Contains(sender)), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [sender] = MakeUserInfoWithEmail(sender, "sender@example.com", "Sender") }));
        _userService.GetUserInfosAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(c => c.Contains(withEmail) || c.Contains(noEmail)),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo>
                {
                    [withEmail] = MakeUserInfoWithEmail(withEmail, "with@example.com", "WithEmail"),
                    [noEmail] = UserInfoStubHelpers.MakeUserInfo(noEmail, displayName: "NoEmail"),
                }));

        var result = await CreateSut().SendRotaMessageAsync(rota.Id, sender, "hello");

        result.Succeeded.Should().BeTrue();
        result.RecipientCount.Should().Be(1, "only the recipient with an email is queued");
        await _emailService.Received(1).SendCoordinatorRotaMessageAsync(
            Arg.Any<CoordinatorRotaMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendRotaMessageAsync_IsolatesPerRecipientFailures_AndStillAudits()
    {
        var rota = MakeRota(out _);
        _signupRepo.GetRotaWithShiftsAsync(rota.Id, Arg.Any<CancellationToken>()).Returns(rota);

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var sender = Guid.NewGuid();
        var shift = MakeShift(rota.Id, dayOffset: 1, startHour: 10);

        _signupRepo.GetActiveByRotaAsync(rota.Id, Arg.Any<CancellationToken>())
            .Returns([
                MakeSignup(userA, shift),
                MakeSignup(userB, shift)
            ]);

        StubUsers(sender, userA, userB);

        // First recipient throws (simulating a transient outbox-write failure);
        // the loop must continue, enqueue the second, and still write the audit row.
        _emailService
            .When(s => s.SendCoordinatorRotaMessageAsync(
                Arg.Is<CoordinatorRotaMessageRequest>(r => r.RecipientEmail == "a@example.com"),
                Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("simulated outbox blip"));

        var result = await CreateSut().SendRotaMessageAsync(rota.Id, sender, "schedule change");

        result.Succeeded.Should().BeTrue("partial dispatch still returns success");
        result.RecipientCount.Should().Be(1, "only the surviving enqueue counts as queued");

        await _emailService.Received(1).SendCoordinatorRotaMessageAsync(
            Arg.Is<CoordinatorRotaMessageRequest>(r => r.RecipientEmail == "b@example.com"),
            Arg.Any<CancellationToken>());

        await _auditLog.Received(1).LogAsync(
            AuditAction.CoordinatorRotaMessageSent,
            nameof(Rota),
            rota.Id,
            Arg.Is<string>(d => d.Contains("1 failed") && d.Contains("schedule change")),
            sender,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    [HumansFact]
    public async Task SendRotaMessageAsync_ReturnsFailure_WhenAllRecipientEnqueuesThrow()
    {
        var rota = MakeRota(out _);
        _signupRepo.GetRotaWithShiftsAsync(rota.Id, Arg.Any<CancellationToken>()).Returns(rota);

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var sender = Guid.NewGuid();
        var shift = MakeShift(rota.Id, dayOffset: 1, startHour: 10);

        _signupRepo.GetActiveByRotaAsync(rota.Id, Arg.Any<CancellationToken>())
            .Returns([
                MakeSignup(userA, shift),
                MakeSignup(userB, shift)
            ]);

        StubUsers(sender, userA, userB);

        // Every recipient's enqueue throws — no email gets queued.
        _emailService
            .When(s => s.SendCoordinatorRotaMessageAsync(
                Arg.Any<CoordinatorRotaMessageRequest>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("simulated outbox outage"));

        var result = await CreateSut().SendRotaMessageAsync(rota.Id, sender, "schedule change");

        result.Succeeded.Should().BeFalse(
            "queued == 0 && failed > 0 must surface as Failure so the controller does not render a misleading success toast");
        result.Error.Should().Contain("Failed to enqueue");

        // Audit row still written so the failures are forensically visible.
        await _auditLog.Received(1).LogAsync(
            AuditAction.CoordinatorRotaMessageSent,
            nameof(Rota),
            rota.Id,
            Arg.Is<string>(d => d.Contains("2 failed")),
            sender,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    private static Rota MakeRota(out EventSettings es)
    {
        es = new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = "Test Event",
            TimeZoneId = "UTC",
            GateOpeningDate = new LocalDate(2026, 7, 1),
            BuildStartOffset = -7,
            EventEndOffset = 7,
            StrikeEndOffset = 9,
            IsShiftBrowsingOpen = true,
            IsActive = true,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        };
        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = es.Id,
            TeamId = Guid.NewGuid(),
            Name = "Test Rota",
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Event,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            EventSettings = es,
        };
        return rota;
    }

    private static Shift MakeShift(Guid rotaId, int dayOffset, int startHour) => new()
    {
        Id = Guid.NewGuid(),
        RotaId = rotaId,
        DayOffset = dayOffset,
        StartTime = new LocalTime(startHour, 0),
        Duration = Duration.FromHours(4),
        MinVolunteers = 1,
        MaxVolunteers = 10,
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
    };

    private static ShiftSignup MakeSignup(Guid userId, Shift shift) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ShiftId = shift.Id,
        Status = SignupStatus.Confirmed,
        Shift = shift,
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
    };

    private static UserInfo MakeUserInfoWithEmail(Guid id, string email, string displayName)
    {
        var user = new User
        {
            Id = id,
            DisplayName = displayName,
            PreferredLanguage = "en",
            Email = email,
            EmailConfirmed = true,
        };
        var ue = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = id,
            Email = email,
            IsPrimary = true,
            IsVerified = true,
        };
        return user.ToUserInfo([ue]);
    }

    /// <summary>
    /// Stubs <see cref="IUserService.GetUserInfosAsync"/> so the sender and all
    /// recipient ids resolve to UserInfo with letter-suffix emails (a@, b@, …).
    /// </summary>
    private void StubUsers(Guid sender, params Guid[] recipients)
    {
        var senderInfo = MakeUserInfoWithEmail(sender, "sender@example.com", "Sender");
        var recipientInfos = new Dictionary<Guid, UserInfo>();
        for (int i = 0; i < recipients.Length; i++)
        {
            var letter = (char)('a' + i);
            recipientInfos[recipients[i]] = MakeUserInfoWithEmail(
                recipients[i], $"{letter}@example.com", $"User{letter}");
        }

        _userService.GetUserInfosAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(c => c.Count == 1 && c.Contains(sender)),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [sender] = senderInfo }));

        _userService.GetUserInfosAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(c => recipients.All(c.Contains)),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(recipientInfos));
    }
}

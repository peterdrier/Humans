using Humans.Shifts.Data;
using Humans.Shifts.Domain;
using Humans.Teams.Domain;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Settings.Contracts;
using Humans.Teams.Contracts;
using Humans.Shifts.Services;
using Humans.Shifts.Tests.Infrastructure;
using Humans.Base.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Users.Contracts;

using Humans.Shifts.Contracts;
namespace Humans.Shifts.Tests.Services;

/// <summary>
/// Orchestration tests for <see cref="RotaCoordinatorMessageService"/>:
/// recipient fan-out, per-recipient shift list, audit log, and the failure shapes
/// (missing message body, missing rota, missing signups). Issue
/// nobodies-collective/Humans#732. Team-scoped variants cover the
/// <c>SendTeamRotasMessageAsync</c> path (fan-out across all current/upcoming
/// rotas in a team, deduped by user).
/// </summary>
public sealed class RotaCoordinatorMessageServiceTests
{
    private readonly IShiftManagementRepository _repo = Substitute.For<IShiftManagementRepository>();
    private readonly ITeamServiceRead _teamService = Substitute.For<ITeamServiceRead>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly ShiftsEmails _emailMessages = TestShiftsEmails.Create();
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 6, 15, 12, 0));

    // Calendars for the EventSettings rows the test helpers below create, keyed by id —
    // stands in for the Settings section (nobodies-collective/Humans#1630).
    private readonly Dictionary<Guid, EventSettings> _calendars = [];
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();

    private RotaCoordinatorMessageService CreateSut()
    {
        _settingsService.GetEventSettingsByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var found = _calendars.TryGetValue(ci.Arg<Guid>(), out var es);
                return Task.FromResult(found ? ToEventSettingsInfo(es!) : null);
            });
        return new(_repo, _teamService, _userService, _emailService, _emailMessages, _auditLog,
            new EventCalendarResolver(_settingsService), _clock,
            NullLogger<RotaCoordinatorMessageService>.Instance);
    }

    private static EventSettingsInfo ToEventSettingsInfo(EventSettings src) => new(
        Id: src.Id,
        EventName: src.EventName,
        Year: src.Year,
        TimeZoneId: src.TimeZoneId,
        GateOpeningDate: src.GateOpeningDate,
        BuildStartOffset: src.BuildStartOffset,
        EventEndOffset: src.EventEndOffset,
        StrikeEndOffset: src.StrikeEndOffset,
        FirstCrewStartOffset: src.FirstCrewStartOffset,
        SetupWeekStartOffset: src.SetupWeekStartOffset,
        PreEventWeekStartOffset: src.PreEventWeekStartOffset,
        FinishingWeekendStartOffset: src.FinishingWeekendStartOffset,
        EarlyEntryCapacity: new Dictionary<int, int>(src.EarlyEntryCapacity),
        BarriosEarlyEntryAllocation: src.BarriosEarlyEntryAllocation is null
            ? null : new Dictionary<int, int>(src.BarriosEarlyEntryAllocation),
        EarlyEntryClose: src.EarlyEntryClose);

    [HumansFact]
    public async Task SendRotaMessageAsync_RejectsBlankMessage()
    {
        var result = await CreateSut().SendRotaMessageAsync(Guid.NewGuid(), Guid.NewGuid(), "   ", includeShifts: true, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("required", "blank body must surface a clear error");
        await _emailService.DidNotReceiveWithAnyArgs().SendAsync(null!);
        await _auditLog.DidNotReceiveWithAnyArgs().LogAsync(
            default, null!, Guid.Empty, null!, Guid.Empty);
    }

    [HumansFact]
    public async Task SendRotaMessageAsync_ReturnsFailure_WhenRotaMissing()
    {
        var rotaId = Guid.NewGuid();
        _repo.GetRotaAsync(rotaId, RotaReadShape.View, Arg.Any<CancellationToken>())
            .Returns((Rota?)null);

        var result = await CreateSut().SendRotaMessageAsync(rotaId, Guid.NewGuid(), "hello", includeShifts: true, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("not found");
        await _emailService.DidNotReceiveWithAnyArgs().SendAsync(null!);
    }

    [HumansFact]
    public async Task SendRotaMessageAsync_ReturnsFailure_WhenNoActiveSignups()
    {
        var rota = MakeRota(out _);
        _repo.GetRotaAsync(rota.Id, RotaReadShape.View, Arg.Any<CancellationToken>()).Returns(rota);

        var result = await CreateSut().SendRotaMessageAsync(rota.Id, Guid.NewGuid(), "hello", includeShifts: true, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("no active signups");
    }

    [HumansFact]
    public async Task SendRotaMessageAsync_FansOut_OneEmailPerDistinctUser()
    {
        var rota = MakeRota(out _);
        _repo.GetRotaAsync(rota.Id, RotaReadShape.View, Arg.Any<CancellationToken>()).Returns(rota);

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var sender = Guid.NewGuid();

        var shift1 = MakeShift(rota.Id, dayOffset: 1, startHour: 10);
        var shift2 = MakeShift(rota.Id, dayOffset: 2, startHour: 14);

        // userA has two signups (same shift twice + another shift), userB has one.
        AddSignups(rota, [
            MakeSignup(userA, shift1),
            MakeSignup(userA, shift2),
            MakeSignup(userB, shift1)
        ]);

        StubUsers(sender, userA, userB);

        await CreateSut().SendRotaMessageAsync(rota.Id, sender, "hello team", includeShifts: true, Xunit.TestContext.Current.CancellationToken);

        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.RecipientEmail == "a@example.com"), Arg.Any<CancellationToken>());
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.RecipientEmail == "b@example.com"), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendRotaMessageAsync_PersonalisesShiftListPerRecipient()
    {
        var rota = MakeRota(out _);
        _repo.GetRotaAsync(rota.Id, RotaReadShape.View, Arg.Any<CancellationToken>()).Returns(rota);

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var sender = Guid.NewGuid();

        var earlyShift = MakeShift(rota.Id, dayOffset: 1, startHour: 9);
        var lateShift = MakeShift(rota.Id, dayOffset: 3, startHour: 18);
        var midShift = MakeShift(rota.Id, dayOffset: 2, startHour: 12);

        AddSignups(rota, [
            MakeSignup(userA, lateShift),  // userA: late + early (should sort)
            MakeSignup(userA, earlyShift),
            MakeSignup(userB, midShift) // userB: only midShift
        ]);

        StubUsers(sender, userA, userB);

        // The shift list is <li> rows inside the built body; assert on that rather than
        // on a captured request, because the builder is real and has no interface to mock.
        EmailMessage? captured_A = null;
        EmailMessage? captured_B = null;
        _ = _emailService.SendAsync(
            Arg.Do<EmailMessage>(m =>
            {
                if (string.Equals(m.RecipientEmail, "a@example.com", StringComparison.Ordinal)) captured_A = m;
                else if (string.Equals(m.RecipientEmail, "b@example.com", StringComparison.Ordinal)) captured_B = m;
            }),
            Arg.Any<CancellationToken>());

        await CreateSut().SendRotaMessageAsync(rota.Id, sender, "hello", includeShifts: true, Xunit.TestContext.Current.CancellationToken);

        captured_A.Should().NotBeNull();
        ShiftLines(captured_A!).Should().HaveCount(2, "userA has 2 distinct shifts on this rota");
        ShiftLines(captured_A!)[0].Should().Contain("09:00", "earlier shift must come first");
        ShiftLines(captured_A!)[1].Should().Contain("18:00");

        captured_B.Should().NotBeNull();
        ShiftLines(captured_B!).Should().ContainSingle("userB has only one shift on this rota");
        ShiftLines(captured_B!)[0].Should().Contain("12:00");
    }

    [HumansFact]
    public async Task SendRotaMessageAsync_CarriesSenderEmail_ForReplyTo()
    {
        // Shifts.md: Reply-To is the sending coordinator while From stays the
        // shared address. This section's contribution to that invariant is
        // passing the sender's own address through as SenderEmail — the
        // Email section's factory turns it into the ReplyTo header.
        var rota = MakeRota(out _);
        _repo.GetRotaAsync(rota.Id, RotaReadShape.View, Arg.Any<CancellationToken>()).Returns(rota);

        var userA = Guid.NewGuid();
        var sender = Guid.NewGuid();
        var shift = MakeShift(rota.Id, dayOffset: 1, startHour: 10);
        AddSignups(rota, [MakeSignup(userA, shift)]);
        StubUsers(sender, userA);

        await CreateSut().SendRotaMessageAsync(rota.Id, sender, "hello", includeShifts: true, Xunit.TestContext.Current.CancellationToken);

        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.ReplyTo == "sender@example.com"), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendRotaMessageAsync_WritesOneAuditEntry_WithSenderActor()
    {
        var rota = MakeRota(out _);
        _repo.GetRotaAsync(rota.Id, RotaReadShape.View, Arg.Any<CancellationToken>()).Returns(rota);

        var userA = Guid.NewGuid();
        var sender = Guid.NewGuid();
        var shift = MakeShift(rota.Id, dayOffset: 1, startHour: 10);

        AddSignups(rota, [MakeSignup(userA, shift)]);

        StubUsers(sender, userA);

        var result = await CreateSut().SendRotaMessageAsync(rota.Id, sender, "schedule change", includeShifts: true, Xunit.TestContext.Current.CancellationToken);

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
        _repo.GetRotaAsync(rota.Id, RotaReadShape.View, Arg.Any<CancellationToken>()).Returns(rota);

        var withEmail = Guid.NewGuid();
        var noEmail = Guid.NewGuid();
        var sender = Guid.NewGuid();
        var shift = MakeShift(rota.Id, dayOffset: 1, startHour: 10);

        AddSignups(rota, [
            MakeSignup(withEmail, shift),
            MakeSignup(noEmail, shift)
        ]);

        // sender + withEmail have addresses; noEmail's UserInfo has an empty email.
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

        var result = await CreateSut().SendRotaMessageAsync(rota.Id, sender, "hello", includeShifts: true, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.RecipientCount.Should().Be(1, "only the recipient with an email is queued");
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == "coordinator_rota_message"), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendRotaMessageAsync_IsolatesPerRecipientFailures_AndStillAudits()
    {
        var rota = MakeRota(out _);
        _repo.GetRotaAsync(rota.Id, RotaReadShape.View, Arg.Any<CancellationToken>()).Returns(rota);

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var sender = Guid.NewGuid();
        var shift = MakeShift(rota.Id, dayOffset: 1, startHour: 10);

        AddSignups(rota, [
            MakeSignup(userA, shift),
            MakeSignup(userB, shift)
        ]);

        StubUsers(sender, userA, userB);

        // First recipient throws (simulating a transient outbox-write failure);
        // the loop must continue, enqueue the second, and still write the audit row.
        _emailService
            .When(s => s.SendAsync(
                Arg.Is<EmailMessage>(m => m.RecipientEmail == "a@example.com"), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("simulated outbox blip"));

        var result = await CreateSut().SendRotaMessageAsync(rota.Id, sender, "schedule change", includeShifts: true, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue("partial dispatch still returns success");
        result.RecipientCount.Should().Be(1, "only the surviving enqueue counts as queued");

        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.RecipientEmail == "b@example.com"), Arg.Any<CancellationToken>());

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
        _repo.GetRotaAsync(rota.Id, RotaReadShape.View, Arg.Any<CancellationToken>()).Returns(rota);

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var sender = Guid.NewGuid();
        var shift = MakeShift(rota.Id, dayOffset: 1, startHour: 10);

        AddSignups(rota, [
            MakeSignup(userA, shift),
            MakeSignup(userB, shift)
        ]);

        StubUsers(sender, userA, userB);

        // Every recipient's enqueue throws — no email gets queued.
        _emailService
            .When(s => s.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("simulated outbox outage"));

        var result = await CreateSut().SendRotaMessageAsync(rota.Id, sender, "schedule change", includeShifts: true, Xunit.TestContext.Current.CancellationToken);

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

    // ============================================================
    // SendTeamRotasMessageAsync — team-scoped fan-out across all
    // current/upcoming rotas in a team.
    // ============================================================

    [HumansFact]
    public async Task SendTeamRotasMessageAsync_RejectsBlankMessage()
    {
        var result = await CreateSut().SendTeamRotasMessageAsync(Guid.NewGuid(), Guid.NewGuid(), "   ", includeShifts: true, TeamRotasAudienceFilter.Default, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("required");
        await _emailService.DidNotReceiveWithAnyArgs().SendAsync(null!);
    }

    [HumansFact]
    public async Task SendTeamRotasMessageAsync_ReturnsFailure_WhenTeamMissing()
    {
        var teamId = Guid.NewGuid();
        _teamService.GetTeamAsync(teamId, Arg.Any<CancellationToken>()).Returns((TeamInfo?)null);

        var result = await CreateSut().SendTeamRotasMessageAsync(teamId, Guid.NewGuid(), "hello", includeShifts: true, TeamRotasAudienceFilter.Default, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("Team not found");
    }

    [HumansFact]
    public async Task SendTeamRotasMessageAsync_ReturnsFailure_WhenNoActiveEvent()
    {
        var teamId = StubTeam();
        // No StubEvent() call — _settingsService.GetActiveEventSettingsAsync defaults to null.

        var result = await CreateSut().SendTeamRotasMessageAsync(teamId, Guid.NewGuid(), "hello", includeShifts: true, TeamRotasAudienceFilter.Default, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("No rota in this team matches that selection");
    }

    [HumansFact]
    public async Task SendTeamRotasMessageAsync_ExcludesRotasWithNoFutureShifts()
    {
        var teamId = StubTeam();
        var es = StubEvent();

        var pastShift = MakeShift(Guid.Empty, dayOffset: -30, startHour: 10);
        var futureShift = MakeShift(Guid.Empty, dayOffset: 30, startHour: 14);

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        var pastOnlyRota = MakeTeamRota(teamId, es, "PastOnly", [(pastShift, [userA])]);
        var futureRota = MakeTeamRota(teamId, es, "Future", [(futureShift, [userB])]);

        _repo.GetRotasAsync(es.Id, Arg.Any<IReadOnlyCollection<Guid>>(), RotaReadShape.View, Arg.Any<CancellationToken>())
            .Returns([pastOnlyRota, futureRota]);

        var sender = Guid.NewGuid();
        // After past-only-rota filtering, only userB survives as a recipient,
        // so StubUsers is called with userB alone. The past-only rota's userA
        // is referenced above only to populate the filtered-out rota.
        _ = userA;
        StubUsers(sender, userB);

        await CreateSut().SendTeamRotasMessageAsync(teamId, sender, "hello", includeShifts: true, TeamRotasAudienceFilter.Default, Xunit.TestContext.Current.CancellationToken);

        // Exactly one email queued, to userB; past-only rota produced no work.
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == "coordinator_team_rotas_message"), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendTeamRotasMessageAsync_FiltersInactiveSignupStatuses()
    {
        var teamId = StubTeam();
        var es = StubEvent();

        var shift = MakeShift(Guid.Empty, dayOffset: 30, startHour: 10);
        var sender = Guid.NewGuid();
        var confirmed = Guid.NewGuid();
        var pending = Guid.NewGuid();
        var bailed = Guid.NewGuid();

        var rota = MakeTeamRota(teamId, es, "Rota1", [(shift, [confirmed, pending, bailed])]);
        // Override the default Confirmed status on two of the three signups.
        rota.Shifts.First().ShiftSignups.First(s => s.UserId == pending).Status = SignupStatus.Pending;
        rota.Shifts.First().ShiftSignups.First(s => s.UserId == bailed).Status = SignupStatus.Bailed;

        _repo.GetRotasAsync(es.Id, Arg.Any<IReadOnlyCollection<Guid>>(), RotaReadShape.View, Arg.Any<CancellationToken>())
            .Returns([rota]);

        // Bailed user is filtered before user lookup, so only the two active
        // recipients are stubbed; if the service ever called GetUserInfosAsync
        // for the bailed id, the test would surface that via the wrong count.
        StubUsers(sender, confirmed, pending);

        var result = await CreateSut().SendTeamRotasMessageAsync(teamId, sender, "hello", includeShifts: true, TeamRotasAudienceFilter.Default, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.RecipientCount.Should().Be(2, "bailed is excluded; pending + confirmed are kept");
        await _emailService.Received(2).SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == "coordinator_team_rotas_message"), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendTeamRotasMessageAsync_DedupesRecipient_AcrossMultipleRotas()
    {
        var teamId = StubTeam();
        var es = StubEvent();

        var userA = Guid.NewGuid();
        var sender = Guid.NewGuid();

        var shiftR1 = MakeShift(Guid.Empty, dayOffset: 30, startHour: 9);
        var shiftR2 = MakeShift(Guid.Empty, dayOffset: 31, startHour: 14);

        var rotaA = MakeTeamRota(teamId, es, "Aardvark", [(shiftR1, [userA])]);
        var rotaB = MakeTeamRota(teamId, es, "Beaver", [(shiftR2, [userA])]);

        _repo.GetRotasAsync(es.Id, Arg.Any<IReadOnlyCollection<Guid>>(), RotaReadShape.View, Arg.Any<CancellationToken>())
            .Returns([rotaA, rotaB]);

        StubUsers(sender, userA);

        var result = await CreateSut().SendTeamRotasMessageAsync(teamId, sender, "hello", includeShifts: true, TeamRotasAudienceFilter.Default, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.RecipientCount.Should().Be(1, "user appears in two rotas but should receive exactly one email");
        result.RotaCount.Should().Be(2);

        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m =>
                m.RecipientEmail == "a@example.com"
                && m.HtmlBody.Contains("Aardvark", StringComparison.Ordinal)
                && m.HtmlBody.Contains("Beaver", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendTeamRotasMessageAsync_GroupsShiftsByRota_InAlphabeticalRotaOrder()
    {
        var teamId = StubTeam();
        var es = StubEvent();
        var userA = Guid.NewGuid();
        var sender = Guid.NewGuid();

        var shiftZ = MakeShift(Guid.Empty, dayOffset: 30, startHour: 9);
        var shiftA = MakeShift(Guid.Empty, dayOffset: 31, startHour: 14);

        var zRota = MakeTeamRota(teamId, es, "Zebra", [(shiftZ, [userA])]);
        var aRota = MakeTeamRota(teamId, es, "Antelope", [(shiftA, [userA])]);

        _repo.GetRotasAsync(es.Id, Arg.Any<IReadOnlyCollection<Guid>>(), RotaReadShape.View, Arg.Any<CancellationToken>())
            .Returns([zRota, aRota]);

        StubUsers(sender, userA);

        EmailMessage? captured = null;
        _ = _emailService.SendAsync(Arg.Do<EmailMessage>(m => captured = m), Arg.Any<CancellationToken>());

        await CreateSut().SendTeamRotasMessageAsync(teamId, sender, "hello", includeShifts: true, TeamRotasAudienceFilter.Default, Xunit.TestContext.Current.CancellationToken);

        captured.Should().NotBeNull();
        captured!.HtmlBody.IndexOf("Antelope", StringComparison.Ordinal).Should().BeGreaterThanOrEqualTo(0);
        captured.HtmlBody.IndexOf("Antelope", StringComparison.Ordinal).Should().BeLessThan(
            captured.HtmlBody.IndexOf("Zebra", StringComparison.Ordinal),
            "rotas grouped alphabetically by name");
    }

    [HumansFact]
    public async Task SendTeamRotasMessageAsync_CarriesSenderEmail_ForReplyTo()
    {
        // Same header-half invariant as the per-rota path: SenderEmail rides
        // through so the Email section's factory sets ReplyTo to the sender.
        var teamId = StubTeam();
        var es = StubEvent();
        var userA = Guid.NewGuid();
        var sender = Guid.NewGuid();

        var shift = MakeShift(Guid.Empty, dayOffset: 30, startHour: 10);
        var rota = MakeTeamRota(teamId, es, "Rota1", [(shift, [userA])]);

        _repo.GetRotasAsync(es.Id, Arg.Any<IReadOnlyCollection<Guid>>(), RotaReadShape.View, Arg.Any<CancellationToken>())
            .Returns([rota]);
        StubUsers(sender, userA);

        await CreateSut().SendTeamRotasMessageAsync(teamId, sender, "hello", includeShifts: true, TeamRotasAudienceFilter.Default, Xunit.TestContext.Current.CancellationToken);

        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.ReplyTo == "sender@example.com"), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendTeamRotasMessageAsync_WritesOneAuditEntry_OnTeamEntity()
    {
        var teamId = StubTeam();
        var es = StubEvent();
        var userA = Guid.NewGuid();
        var sender = Guid.NewGuid();

        var shift = MakeShift(Guid.Empty, dayOffset: 30, startHour: 10);
        var rota = MakeTeamRota(teamId, es, "Rota1", [(shift, [userA])]);

        _repo.GetRotasAsync(es.Id, Arg.Any<IReadOnlyCollection<Guid>>(), RotaReadShape.View, Arg.Any<CancellationToken>())
            .Returns([rota]);
        StubUsers(sender, userA);

        await CreateSut().SendTeamRotasMessageAsync(teamId, sender, "schedule change", includeShifts: true, TeamRotasAudienceFilter.Default, Xunit.TestContext.Current.CancellationToken);

        await _auditLog.Received(1).LogAsync(
            AuditAction.CoordinatorTeamRotasMessageSent,
            nameof(Team),
            teamId,
            Arg.Is<string>(d => d.Contains("schedule change") && d.Contains("Test Team")),
            sender,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    [HumansFact]
    public async Task SendTeamRotasMessageAsync_IsolatesPerRecipientFailures()
    {
        var teamId = StubTeam();
        var es = StubEvent();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var sender = Guid.NewGuid();

        var shift = MakeShift(Guid.Empty, dayOffset: 30, startHour: 10);
        var rota = MakeTeamRota(teamId, es, "Rota1", [(shift, [userA, userB])]);

        _repo.GetRotasAsync(es.Id, Arg.Any<IReadOnlyCollection<Guid>>(), RotaReadShape.View, Arg.Any<CancellationToken>())
            .Returns([rota]);
        StubUsers(sender, userA, userB);

        _emailService
            .When(s => s.SendAsync(
                Arg.Is<EmailMessage>(m => m.RecipientEmail == "a@example.com"), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("simulated outbox blip"));

        var result = await CreateSut().SendTeamRotasMessageAsync(teamId, sender, "hi", includeShifts: true, TeamRotasAudienceFilter.Default, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue("partial dispatch returns success");
        result.RecipientCount.Should().Be(1);
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.RecipientEmail == "b@example.com"), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetTeamRotasRecipientPreviewAsync_ReturnsDedupedNamesAndRotaCount()
    {
        var teamId = StubTeam();
        var es = StubEvent();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        var shift1 = MakeShift(Guid.Empty, dayOffset: 30, startHour: 10);
        var shift2 = MakeShift(Guid.Empty, dayOffset: 31, startHour: 12);
        var rota1 = MakeTeamRota(teamId, es, "R1", [(shift1, [userA, userB])]);
        var rota2 = MakeTeamRota(teamId, es, "R2", [(shift2, [userA])]);

        _repo.GetRotasAsync(es.Id, Arg.Any<IReadOnlyCollection<Guid>>(), RotaReadShape.View, Arg.Any<CancellationToken>())
            .Returns([rota1, rota2]);

        _userService.GetUserInfosAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(c => c.Contains(userA) && c.Contains(userB)),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo>
                {
                    [userA] = MakeUserInfoWithEmail(userA, "a@example.com", "Alice"),
                    [userB] = MakeUserInfoWithEmail(userB, "b@example.com", "Bob"),
                }));

        var preview = await CreateSut().GetTeamRotasRecipientPreviewAsync(teamId, TeamRotasAudienceFilter.Default, Xunit.TestContext.Current.CancellationToken);

        preview.RotaCount.Should().Be(2);
        preview.RecipientNames.Should().BeEquivalentTo(["Alice", "Bob"]);
    }

    [HumansFact]
    public async Task SendRotaMessageAsync_DropsShiftSection_WhenIncludeShiftsCleared()
    {
        var rota = MakeRota(out _);
        _repo.GetRotaAsync(rota.Id, RotaReadShape.View, Arg.Any<CancellationToken>()).Returns(rota);

        var userA = Guid.NewGuid();
        var sender = Guid.NewGuid();
        AddSignups(rota, [MakeSignup(userA, MakeShift(rota.Id, dayOffset: 1, startHour: 10))]);
        StubUsers(sender, userA);

        var result = await CreateSut().SendRotaMessageAsync(
            rota.Id, sender, "thank you", includeShifts: false, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => !m.HtmlBody.Contains("ShiftsIntro", StringComparison.Ordinal)
                                      && !m.HtmlBody.Contains("<li>", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendTeamRotasMessageAsync_ReachesPastRotas_WhenUpcomingOnlyCleared()
    {
        var teamId = StubTeam();
        var es = StubEvent();

        var userA = Guid.NewGuid();
        var sender = Guid.NewGuid();
        var pastRota = MakeTeamRota(
            teamId, es, "PastOnly", [(MakeShift(Guid.Empty, dayOffset: -30, startHour: 10), [userA])]);
        pastRota.Period = RotaPeriod.Build;

        _repo.GetRotasAsync(es.Id, Arg.Any<IReadOnlyCollection<Guid>>(), RotaReadShape.View, Arg.Any<CancellationToken>())
            .Returns([pastRota]);
        StubUsers(sender, userA);

        var result = await CreateSut().SendTeamRotasMessageAsync(
            teamId, sender, "thank you", includeShifts: false,
            new TeamRotasAudienceFilter(UpcomingOnly: false, IncludeBuild: true, IncludeEvent: true, IncludeStrike: true),
            Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue("a post-event thank-you must reach rotas that have already finished");
        result.RecipientCount.Should().Be(1);
    }

    [HumansFact]
    public async Task SendTeamRotasMessageAsync_ExcludesRotasOutsideSelectedPeriods()
    {
        var teamId = StubTeam();
        var es = StubEvent();

        var buildUser = Guid.NewGuid();
        var strikeUser = Guid.NewGuid();
        var sender = Guid.NewGuid();

        var buildRota = MakeTeamRota(
            teamId, es, "Build", [(MakeShift(Guid.Empty, dayOffset: -30, startHour: 10), [buildUser])]);
        buildRota.Period = RotaPeriod.Build;
        var strikeRota = MakeTeamRota(
            teamId, es, "Strike", [(MakeShift(Guid.Empty, dayOffset: 70, startHour: 10), [strikeUser])]);
        strikeRota.Period = RotaPeriod.Strike;

        _repo.GetRotasAsync(es.Id, Arg.Any<IReadOnlyCollection<Guid>>(), RotaReadShape.View, Arg.Any<CancellationToken>())
            .Returns([buildRota, strikeRota]);
        // Only the Build rota survives the filter, so only its user is ever looked up.
        _ = strikeUser;
        StubUsers(sender, buildUser);

        var result = await CreateSut().SendTeamRotasMessageAsync(
            teamId, sender, "thank you", includeShifts: false,
            new TeamRotasAudienceFilter(UpcomingOnly: false, IncludeBuild: true, IncludeEvent: false, IncludeStrike: false),
            Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.RotaCount.Should().Be(1);
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.RecipientEmail == "a@example.com"), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetTeamRotasRecipientPreviewAsync_IgnoresPeriodFlags_WhenUpcomingOnly()
    {
        var teamId = StubTeam();
        var es = StubEvent();
        var userA = Guid.NewGuid();

        var rota = MakeTeamRota(
            teamId, es, "Future", [(MakeShift(Guid.Empty, dayOffset: 30, startHour: 10), [userA])]);
        rota.Period = RotaPeriod.Event;

        _repo.GetRotasAsync(es.Id, Arg.Any<IReadOnlyCollection<Guid>>(), RotaReadShape.View, Arg.Any<CancellationToken>())
            .Returns([rota]);
        _userService.GetUserInfosAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(c => c.Contains(userA)), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [userA] = MakeUserInfoWithEmail(userA, "a@example.com", "Alice") }));

        // Every period box cleared: the compose form hides them under "upcoming",
        // so they must not silently empty the audience.
        var preview = await CreateSut().GetTeamRotasRecipientPreviewAsync(
            teamId,
            new TeamRotasAudienceFilter(UpcomingOnly: true, IncludeBuild: false, IncludeEvent: false, IncludeStrike: false),
            Xunit.TestContext.Current.CancellationToken);

        preview.RotaCount.Should().Be(1);
        preview.RecipientNames.Should().BeEquivalentTo(["Alice"]);
    }

    private Guid StubTeam()
    {
        var teamId = Guid.NewGuid();
        var team = new TeamInfo(
            teamId, "Test Team", null, "test-team",
            IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
            RequiresApproval: false, IsPublicPage: false, IsHidden: false,
            IsPromotedToDirectory: false, CreatedAt: Instant.MinValue,
            Members: []);
        _teamService.GetTeamAsync(teamId, Arg.Any<CancellationToken>()).Returns(team);
        return teamId;
    }

    private EventSettings StubEvent()
    {
        var es = new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = "Test Event",
            TimeZoneId = "UTC",
            GateOpeningDate = new LocalDate(2026, 6, 1),
            BuildStartOffset = -30,
            EventEndOffset = 60,
            StrikeEndOffset = 90,
            IsShiftBrowsingOpen = true,
            IsActive = true,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        };
        _calendars[es.Id] = es;
        _settingsService.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(ToEventSettingsInfo(es));
        return es;
    }

    /// <summary>
    /// Builds a team-scoped Rota with EventSettings wired up and Shift+ShiftSignup
    /// collections populated as the team-level path expects (via Rota.Shifts →
    /// Shift.ShiftSignups). Each tuple is (shift, recipient user ids); all signups
    /// default to Confirmed status.
    /// </summary>
    private static void AddSignups(Rota rota, IReadOnlyList<ShiftSignup> signups)
    {
        foreach (var signup in signups)
        {
            var shift = signup.Shift;
            shift.RotaId = rota.Id;
            if (rota.Shifts.All(existing => existing.Id != shift.Id))
                rota.Shifts.Add(shift);
            if (shift.ShiftSignups.All(existing => existing.Id != signup.Id))
                shift.ShiftSignups.Add(signup);
        }
    }

    private Rota MakeTeamRota(
        Guid teamId,
        EventSettings es,
        string name,
        IReadOnlyList<(Shift Shift, Guid[] UserIds)> shiftsWithSignups)
    {
        _calendars[es.Id] = es;
        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = es.Id,
            TeamId = teamId,
            Name = name,
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Event,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            EventSettings = es,
        };
        foreach (var (shift, userIds) in shiftsWithSignups)
        {
            shift.RotaId = rota.Id;
            foreach (var uid in userIds)
            {
                shift.ShiftSignups.Add(MakeSignup(uid, shift));
            }
            rota.Shifts.Add(shift);
        }
        return rota;
    }

    private Rota MakeRota(out EventSettings es)
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
        _calendars[es.Id] = es;
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
            // Mirrors CopyNamesToUser's dual-write from Profile onto User (#1097) —
            // UserInfo.BurnerName reads User.BurnerName only (#1098).
            BurnerName = displayName,
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

    /// <summary>
    /// The shift labels the builder wrote into the body, one per <c>&lt;li&gt;</c> row and in
    /// body order. The list is the only place the per-recipient shift set is observable now
    /// that the service builds its own message.
    /// </summary>
    private static IReadOnlyList<string> ShiftLines(EmailMessage message) =>
    [
        .. message.HtmlBody
            .Split("<li>", StringSplitOptions.None)
            .Skip(1)
            .Select(part => part[..part.IndexOf("</li>", StringComparison.Ordinal)])
    ];
}

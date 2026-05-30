using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Models;
using Humans.Web.Models.Shifts;
using NodaTime;
using NSubstitute;

namespace Humans.Web.Tests.Models.Shifts;

/// <summary>
/// Tests for <see cref="ShiftAdminPageBuilder"/>'s private "Incomplete onboarding"
/// Pending-list filter, reached only through <see cref="ShiftAdminPageBuilder.BuildAsync"/>
/// when <see cref="ShiftAdminPageRequest.IncompleteOnboarding"/> is true. The filter keeps
/// only pending signups whose user is NOT in the "has all required consents" set returned by
/// <see cref="IMembershipCalculator.GetUsersWithAllRequiredConsentsForTeamAsync"/>.
///
/// Ported from the deleted
/// tests/Humans.Application.Tests/Services/Shifts/ShiftSignupServiceFilterIncompleteOnboardingTests.cs
/// (PR #820 moved the logic from the service into this Web-layer builder).
/// </summary>
public sealed class ShiftAdminPageBuilderTests
{
    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    private readonly IShiftManagementService _shiftManagement = Substitute.For<IShiftManagementService>();
    private readonly IMembershipCalculator _membership = Substitute.For<IMembershipCalculator>();
    private readonly IUserServiceRead _userService = Substitute.For<IUserServiceRead>();
    private readonly ITeamServiceRead _teamService = Substitute.For<ITeamServiceRead>();

    private static readonly TeamInfo Department = new(
        Guid.NewGuid(), "Test Department", null, "test-dept",
        IsActive: true, IsSystemTeam: false, SystemTeamType.None, RequiresApproval: false,
        IsPublicPage: true, IsHidden: false, IsPromotedToDirectory: false,
        CreatedAt: TestNow, Members: []);

    private static readonly EventSettings Event = new()
    {
        Id = Guid.NewGuid(),
        EventName = "Test Event 2026",
        TimeZoneId = "Europe/Madrid",
        GateOpeningDate = new LocalDate(2026, 7, 1),
        BuildStartOffset = -14,
        EventEndOffset = 6,
        StrikeEndOffset = 9,
        IsActive = true,
        CreatedAt = TestNow,
        UpdatedAt = TestNow
    };

    public ShiftAdminPageBuilderTests()
    {
        // Stub every collaborator BuildAsync touches on the IncompleteOnboarding=true
        // path so it runs to completion without an NRE. GetRotasByDepartmentAsync is
        // re-stubbed per test with the scenario's rotas.
        _shiftManagement.GetTagsAsync().Returns([]);
        _shiftManagement.GetStaffingSnapshotAsync(Event.Id, Department.Id)
            .Returns(ShiftStaffingSnapshot.Empty);
        _shiftManagement.GetShiftProfileAsync(Arg.Any<Guid>())
            .Returns((VolunteerEventProfile?)null);

        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>());

        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());
    }

    [HumansFact]
    public async Task EmptyPending_DoesNotCallMembership()
    {
        // A rota whose single shift has no pending signups.
        var shift = MakeShift();
        StubRotas(MakeRota(shift));

        var vm = await BuildAsync();

        vm.PendingSignups.Should().BeEmpty();
        await _membership.DidNotReceiveWithAnyArgs()
            .GetUsersWithAllRequiredConsentsForTeamAsync(null!, Guid.Empty, CancellationToken.None);
    }

    [HumansFact]
    public async Task KeepsOnlyUsersWithoutAllConsents()
    {
        var withConsents = Guid.NewGuid();
        var withoutConsents = Guid.NewGuid();

        var shift = MakeShift(MakePending(withConsents), MakePending(withoutConsents));
        StubRotas(MakeRota(shift));

        _membership.GetUsersWithAllRequiredConsentsForTeamAsync(
                Arg.Any<IEnumerable<Guid>>(), SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { withConsents });

        var vm = await BuildAsync();

        vm.PendingSignups.Should().ContainSingle()
            .Which.UserId.Should().Be(withoutConsents);
    }

    [HumansFact]
    public async Task AllHaveConsents_ReturnsEmpty()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();

        var shift = MakeShift(MakePending(u1), MakePending(u2));
        StubRotas(MakeRota(shift));

        _membership.GetUsersWithAllRequiredConsentsForTeamAsync(
                Arg.Any<IEnumerable<Guid>>(), SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { u1, u2 });

        var vm = await BuildAsync();

        vm.PendingSignups.Should().BeEmpty();
    }

    [HumansFact]
    public async Task NoneHaveConsents_KeepsAll()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();

        var shift = MakeShift(MakePending(u1), MakePending(u2));
        StubRotas(MakeRota(shift));

        _membership.GetUsersWithAllRequiredConsentsForTeamAsync(
                Arg.Any<IEnumerable<Guid>>(), SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());

        var vm = await BuildAsync();

        vm.PendingSignups.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task DistinctUserIdsPassedToMembership()
    {
        var sharedUser = Guid.NewGuid();

        var shift = MakeShift(
            MakePending(sharedUser),
            MakePending(sharedUser),
            MakePending(Guid.NewGuid()));
        StubRotas(MakeRota(shift));

        _membership.GetUsersWithAllRequiredConsentsForTeamAsync(
                Arg.Any<IEnumerable<Guid>>(), SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());

        await BuildAsync();

        await _membership.Received(1).GetUsersWithAllRequiredConsentsForTeamAsync(
            // ReSharper disable once PossibleMultipleEnumeration — Arg.Is requires an expression-tree lambda.
            Arg.Is<IEnumerable<Guid>>(ids => ids.Distinct().Count() == ids.Count() && ids.Count() == 2),
            SystemTeamIds.Volunteers,
            Arg.Any<CancellationToken>());
    }

    // ============================================================
    // Helpers
    // ============================================================

    private void StubRotas(params Rota[] rotas) =>
        _shiftManagement.GetRotasByDepartmentAsync(Department.Id, Event.Id)
            .Returns(rotas.ToList());

    private Task<ShiftAdminViewModel> BuildAsync()
    {
        var builder = new ShiftAdminPageBuilder(_shiftManagement, _membership, _userService, _teamService);
        return builder.BuildAsync(new ShiftAdminPageRequest(
            Department,
            Event,
            CanManage: true,
            CanApprove: true,
            CanViewMedical: false,
            IncompleteOnboarding: true,
            Now: TestNow));
    }

    private static Rota MakeRota(params Shift[] shifts)
    {
        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = Event.Id,
            TeamId = Department.Id,
            Name = "Test Rota",
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Event,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        foreach (var shift in shifts)
            rota.Shifts.Add(shift);
        return rota;
    }

    private static Shift MakeShift(params ShiftSignup[] signups)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = Guid.NewGuid(),
            DayOffset = 1,
            StartTime = new LocalTime(8, 0),
            Duration = Duration.FromHours(4),
            MinVolunteers = 2,
            MaxVolunteers = 5,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        foreach (var signup in signups)
            shift.ShiftSignups.Add(signup);
        return shift;
    }

    private static ShiftSignup MakePending(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ShiftId = Guid.NewGuid(),
        Status = SignupStatus.Pending,
        CreatedAt = TestNow,
        UpdatedAt = TestNow
    };
}

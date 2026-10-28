using Humans.Cantina.Contracts;
using Humans.Shifts.Domain;
using AwesomeAssertions;
using Humans.Governance.Contracts;
using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Shifts.Models;
using Humans.Shifts.Services.Dtos;
using NodaTime;
using NSubstitute;
using Humans.Users.Contracts;

namespace Humans.Shifts.Tests.Models;

/// <summary>
/// The private "Incomplete onboarding" pending filter, reached only through
/// <see cref="ShiftAdminPageBuilder.BuildAsync"/>: it keeps pending signups whose user lacks a required consent.
/// </summary>
public sealed class ShiftAdminPageBuilderTests
{
    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    private readonly IShiftManagement _shiftManagement = Substitute.For<IShiftManagement>();
    private readonly IMembershipCalculator _membership = Substitute.For<IMembershipCalculator>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly ICantinaService _cantina = Substitute.For<ICantinaService>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();

    private static readonly TeamInfo Department = new(
        Guid.NewGuid(), "Test Department", null, "test-dept",
        IsActive: true, IsSystemTeam: false, SystemTeamType.None, RequiresApproval: false,
        IsPublicPage: true, IsHidden: false, IsPromotedToDirectory: false,
        CreatedAt: TestNow, Members: []);

    private static readonly BurnSettingsInfo Event = new(
        Id: Guid.NewGuid(),
        EventName: "Test Event 2026",
        Year: 2026,
        TimeZoneId: "Europe/Madrid",
        GateOpeningDate: new LocalDate(2026, 7, 1),
        BuildStartOffset: -14,
        EventEndOffset: 6,
        StrikeEndOffset: 9,
        FirstCrewStartOffset: -14,
        SetupWeekStartOffset: -10,
        PreEventWeekStartOffset: -7,
        FinishingWeekendStartOffset: -3,
        EarlyEntryCapacity: new Dictionary<int, int>(),
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null,
        IsShiftBrowsingOpen: true);

    public ShiftAdminPageBuilderTests()
    {
        // Stub every collaborator BuildAsync touches on the IncompleteOnboarding=true
        // path so it runs to completion without an NRE. GetRotasByDepartmentAsync is
        // re-stubbed per test with the scenario's rotas.
        _shiftManagement.GetTagsAsync(ct: Arg.Any<CancellationToken>()).Returns([]);
        _shiftManagement.GetStaffingSnapshotAsync(Event.Id, Department.Id, ct: Arg.Any<CancellationToken>())
            .Returns(ShiftStaffingSnapshot.Empty);
        _shiftManagement.GetShiftProfileAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((ShiftVolunteerProfileInfo?)null);

        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>());
        _cantina.GetDietaryProfilesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, DietaryProfileInfo>());

        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());
    }

    [HumansFact]
    public async Task BuildAsync_PreservesCancellationThroughAllPageReads()
    {
        using var cancellation = new CancellationTokenSource();
        var userId = Guid.NewGuid();
        _shiftManagement.GetRotasByDepartmentAsync(Department.Id, Event.Id, Arg.Any<CancellationToken>())
            .Returns([MakeRota(MakeShift(MakePending(userId)))]);
        _membership.GetUsersWithAllRequiredConsentsForTeamAsync(
                Arg.Any<IEnumerable<Guid>>(), SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());
        var builder = new ShiftAdminPageBuilder(_shiftManagement, _membership, _userService, _cantina, _teamService);

        await builder.BuildAsync(new ShiftAdminPageRequest(
            Department, Event, true, true, false, true, TestNow), cancellation.Token);

        await _shiftManagement.Received(1).GetRotasByDepartmentAsync(Department.Id, Event.Id, cancellation.Token);
        await _membership.Received(1).GetUsersWithAllRequiredConsentsForTeamAsync(
            Arg.Any<IEnumerable<Guid>>(), SystemTeamIds.Volunteers, cancellation.Token);
        await _userService.Received(1).GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), cancellation.Token);
        await _cantina.Received(1).GetDietaryProfilesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), cancellation.Token);
        await _shiftManagement.Received(1).GetShiftProfileAsync(userId, cancellation.Token);
        await _teamService.Received(1).GetTeamsAsync(cancellation.Token);
        await _shiftManagement.Received(1).GetTagsAsync(ct: cancellation.Token);
        await _shiftManagement.Received(1).GetStaffingSnapshotAsync(Event.Id, Department.Id, ct: cancellation.Token);
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
            .GetUsersWithAllRequiredConsentsForTeamAsync(null!, Guid.Empty, Arg.Any<CancellationToken>());
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

    private void StubRotas(params ShiftAdminRota[] rotas) =>
        _shiftManagement.GetRotasByDepartmentAsync(Department.Id, Event.Id)
            .Returns(rotas.ToList());

    private Task<ShiftAdminViewModel> BuildAsync()
    {
        var builder = new ShiftAdminPageBuilder(_shiftManagement, _membership, _userService, _cantina, _teamService);
        return builder.BuildAsync(new ShiftAdminPageRequest(
            Department,
            Event,
            CanManage: true,
            CanApprove: true,
            CanViewMedical: false,
            IncompleteOnboarding: true,
            Now: TestNow));
    }

    private static ShiftAdminRota MakeRota(params ShiftAdminShift[] shifts) => new(
        new RotaInfo(Guid.NewGuid(), Event.Id, Department.Id, "Test Rota", null, null,
            ShiftPriority.Normal, SignupPolicy.Public, RotaPeriod.Event, false, []),
        shifts);

    private static ShiftAdminShift MakeShift(params ShiftAdminSignup[] signups) => new(
        new ShiftInfo(Guid.NewGuid(), Guid.NewGuid(), null, 1, new LocalTime(8, 0), 4, 2, 5, false, false, false),
        signups);

    private static ShiftAdminSignup MakePending(Guid userId) =>
        new(Guid.NewGuid(), userId, null, SignupStatus.Pending, TestNow);
}

using AwesomeAssertions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Governance.Services;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Consent.Contracts;
using Humans.Governance.Contracts;
using Humans.Users.Contracts;

using Humans.Teams.Contracts;
namespace Humans.Governance.Tests.Services;

public class MembershipCalculatorTests
{
    private readonly FakeClock _clock;
    private readonly MembershipCalculator _service;
    private readonly IMembershipQuery _membershipQuery = Substitute.For<IMembershipQuery>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IConsentServiceRead _consentService = Substitute.For<IConsentServiceRead>();
    private readonly ILegalDocumentSyncServiceRead _legalDocumentSyncService = Substitute.For<ILegalDocumentSyncServiceRead>();

    // Seed backing state — section service substitutes read from these maps.
    private readonly Dictionary<Guid, ProfileInfo> _profilesByUserId = new();
    private readonly Dictionary<Guid, UserState> _statesByUserId = new();
    private readonly Dictionary<Guid, List<SeedMembership>> _teamMembershipsByUserId = new();
    private readonly Dictionary<Guid, SeedTeamRow> _teamsById = new();
    private readonly Dictionary<Guid, List<RequiredDocumentVersionSnapshot>> _requiredVersionsByTeam = new();
    private readonly Dictionary<Guid, HashSet<Guid>> _consentedVersionsByUser = new();

    public MembershipCalculatorTests()
    {
        _clock = new FakeClock(Instant.FromUtc(2026, 2, 15, 16, 0));

        var serviceProvider = new ServiceLocatorBuilder()
            .With(_consentService)
            .Build();

        _service = new MembershipCalculator(
            _membershipQuery,
            _userService,
            _legalDocumentSyncService,
            serviceProvider,
            _clock);

        // Wire substitutes to the seed maps so tests can just mutate state.
        _userService.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var userId = ci.Arg<Guid>();
                var profile = _profilesByUserId.GetValueOrDefault(userId);
                return profile is null ? null : WrapInUserInfo(userId, profile, StateOf(userId));
            });

        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ids = ci.Arg<IReadOnlyCollection<Guid>>();
                var map = ids
                    .Where(_profilesByUserId.ContainsKey)
                    .ToDictionary(id => id, id => WrapInUserInfo(id, _profilesByUserId[id], StateOf(id)));
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(map);
            });

        _membershipQuery.GetUserTeamsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var userId = ci.Arg<Guid>();
                var memberships = _teamMembershipsByUserId.GetValueOrDefault(userId) ?? [];
                return Task.FromResult<IReadOnlyList<MembershipTeamSnapshot>>(memberships
                    .Select(m => new MembershipTeamSnapshot(m.TeamId, m.Role, m.Team.SystemTeamType))
                    .ToList());
            });

        _membershipQuery.IsUserMemberOfTeamAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var teamId = ci.ArgAt<Guid>(0);
                var userId = ci.ArgAt<Guid>(1);
                var memberships = _teamMembershipsByUserId.GetValueOrDefault(userId) ?? [];
                return Task.FromResult(memberships.Any(m => m.TeamId == teamId && m.LeftAt == null));
            });

        _membershipQuery.HasAnyActiveAssignmentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        _membershipQuery.GetUserIdsWithActiveAssignmentsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>(new List<Guid>()));

        _legalDocumentSyncService.GetRequiredDocumentVersionsForTeamAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var teamId = ci.Arg<Guid>();
                var versions = _requiredVersionsByTeam.GetValueOrDefault(teamId) ?? [];
                return Task.FromResult<IReadOnlyList<RequiredDocumentVersionSnapshot>>(versions);
            });

        _consentService.GetConsentedVersionIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var userId = ci.Arg<Guid>();
                var set = _consentedVersionsByUser.GetValueOrDefault(userId) ?? [];
                return Task.FromResult<IReadOnlySet<Guid>>(set);
            });

        _consentService.GetConsentMapForUsersAsync(
            Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var userIds = ci.Arg<IReadOnlyList<Guid>>();
                var result = userIds.ToDictionary(
                    id => id,
                    id => (IReadOnlySet<Guid>)(_consentedVersionsByUser.GetValueOrDefault(id) ?? []));
                return Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>>(result);
            });
    }


    [HumansFact]
    public async Task ComputeStatusAsync_NotApprovedProfile_ReturnsPending()
    {
        var userId = Guid.NewGuid();
        SeedProfile(userId, isApproved: false, isSuspended: false);
        SeedActiveRole(userId);

        var result = await _service.ComputeStatusAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(MembershipStatus.Pending);
    }

    [HumansFact]
    public async Task GetMembershipSnapshotAsync_ReturnsConsolidatedState()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        SeedProfile(userId, isApproved: true, isSuspended: false);
        SeedActiveRole(userId);
        SeedVolunteersTeamMember(userId);

        SeedRequiredVersion(SystemTeamIds.Volunteers, versionId, gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(1));

        var snapshot = await _service.GetMembershipSnapshotAsync(userId, Xunit.TestContext.Current.CancellationToken);

        snapshot.RequiredConsentCount.Should().Be(1);
        snapshot.PendingConsentCount.Should().Be(1);
        snapshot.MissingConsentVersionIds.Should().ContainSingle().Which.Should().Be(versionId);
        snapshot.IsVolunteerMember.Should().BeTrue();
        snapshot.Status.Should().Be(MembershipStatus.Inactive);
    }

    // --- GetRequiredTeamIdsForUserAsync tests ---

    [HumansFact]
    public async Task GetRequiredTeamIdsForUserAsync_AlwaysIncludesVolunteers()
    {
        var userId = Guid.NewGuid();

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().Contain(SystemTeamIds.Volunteers);
    }

    [HumansFact]
    public async Task GetRequiredTeamIdsForUserAsync_IncludesCoordinators_WhenUserIsCoordinatorOfUserCreatedTeam()
    {
        var userId = Guid.NewGuid();
        var userTeam = SeedTeam("Geeks", SystemTeamType.None);
        SeedTeamMember(userId, userTeam.Id, TeamMemberRole.Coordinator);

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().Contain(SystemTeamIds.Volunteers);
        result.Should().Contain(SystemTeamIds.Coordinators);
    }

    [HumansFact]
    public async Task GetRequiredTeamIdsForUserAsync_ExcludesCoordinators_WhenUserIsOnlyMember()
    {
        var userId = Guid.NewGuid();
        var userTeam = SeedTeam("Geeks", SystemTeamType.None);
        SeedTeamMember(userId, userTeam.Id, TeamMemberRole.Member);

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().Contain(SystemTeamIds.Volunteers);
        result.Should().NotContain(SystemTeamIds.Coordinators);
    }

    [HumansFact]
    public async Task GetRequiredTeamIdsForUserAsync_ExcludesCoordinators_WhenUserIsCoordinatorOfSystemTeam()
    {
        var userId = Guid.NewGuid();
        // Coordinator of the Volunteers system team should NOT trigger Coordinators eligibility
        var volunteersTeam = SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        SeedTeamMember(userId, volunteersTeam.Id, TeamMemberRole.Coordinator);

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().Contain(SystemTeamIds.Volunteers);
        result.Should().NotContain(SystemTeamIds.Coordinators);
    }

    [HumansFact]
    public async Task GetRequiredTeamIdsForUserAsync_IncludesCurrentTeamMemberships()
    {
        var userId = Guid.NewGuid();
        var geeks = SeedTeam("Geeks", SystemTeamType.None);
        var volunteers = SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        SeedTeamMember(userId, geeks.Id, TeamMemberRole.Member);
        SeedTeamMember(userId, volunteers.Id, TeamMemberRole.Member);

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().Contain(geeks.Id);
        result.Should().Contain(SystemTeamIds.Volunteers);
    }

    // --- GetMembershipSnapshotAsync with Coordinators docs ---

    [HumansFact]
    public async Task GetMembershipSnapshotAsync_IncludesCoordinatorsDocsForCoordinatorUser()
    {
        var userId = Guid.NewGuid();
        SeedProfile(userId, isApproved: true, isSuspended: false);
        SeedActiveRole(userId);

        // User-created team where user is Coordinator
        var geeks = SeedTeam("Geeks", SystemTeamType.None);
        SeedTeamMember(userId, geeks.Id, TeamMemberRole.Coordinator);

        // Volunteers member
        SeedVolunteersTeamMember(userId);

        // Volunteer doc (required)
        var volVersionId = Guid.NewGuid();
        SeedRequiredVersion(SystemTeamIds.Volunteers, volVersionId, gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(1));

        // Coordinators doc (required)
        var coordsVersionId = Guid.NewGuid();
        SeedRequiredVersion(SystemTeamIds.Coordinators, coordsVersionId, gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(1));

        var snapshot = await _service.GetMembershipSnapshotAsync(userId, Xunit.TestContext.Current.CancellationToken);

        // Should include both Volunteers and Coordinators docs
        snapshot.RequiredConsentCount.Should().Be(2);
        snapshot.PendingConsentCount.Should().Be(2);
        snapshot.MissingConsentVersionIds.Should().Contain(volVersionId);
        snapshot.MissingConsentVersionIds.Should().Contain(coordsVersionId);
    }

    [HumansFact]
    public async Task GetMembershipSnapshotAsync_ExcludesCoordinatorsDocs_WhenUserIsNotCoordinator()
    {
        var userId = Guid.NewGuid();
        SeedProfile(userId, isApproved: true, isSuspended: false);
        SeedActiveRole(userId);

        // User is just a member of a user-created team, not a coordinator
        var geeks = SeedTeam("Geeks", SystemTeamType.None);
        SeedTeamMember(userId, geeks.Id, TeamMemberRole.Member);
        SeedVolunteersTeamMember(userId);

        // Volunteer doc
        var volVersionId = Guid.NewGuid();
        SeedRequiredVersion(SystemTeamIds.Volunteers, volVersionId, gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(1));

        // Coordinators doc exists but should NOT appear for non-coordinators
        SeedRequiredVersion(SystemTeamIds.Coordinators, Guid.NewGuid(), gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(1));

        var snapshot = await _service.GetMembershipSnapshotAsync(userId, Xunit.TestContext.Current.CancellationToken);

        // Should only include Volunteers doc, not Coordinators
        snapshot.RequiredConsentCount.Should().Be(1);
        snapshot.PendingConsentCount.Should().Be(1);
        snapshot.MissingConsentVersionIds.Should().ContainSingle().Which.Should().Be(volVersionId);
    }

    // --- GetRequiredTeamIdsForUserAsync: Colaboradors team ---

    [HumansFact]
    public async Task GetRequiredTeamIdsForUserAsync_IncludesColaboradors_WhenUserIsColaborador()
    {
        var userId = Guid.NewGuid();
        SeedVolunteersTeamMember(userId);
        var colaboradorsTeam = SeedTeam("Colaboradors", SystemTeamType.Colaboradors, SystemTeamIds.Colaboradors);
        SeedTeamMember(userId, colaboradorsTeam.Id, TeamMemberRole.Member);

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().Contain(SystemTeamIds.Volunteers);
        result.Should().Contain(SystemTeamIds.Colaboradors);
    }

    [HumansFact]
    public async Task GetRequiredTeamIdsForUserAsync_ExcludesColaboradors_WhenUserIsNotColaborador()
    {
        var userId = Guid.NewGuid();
        SeedVolunteersTeamMember(userId);

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().Contain(SystemTeamIds.Volunteers);
        result.Should().NotContain(SystemTeamIds.Colaboradors);
    }

    // --- ComputeStatusAsync (additional tests) ---

    [HumansFact]
    public async Task ComputeStatusAsync_NoProfile_ReturnsNone()
    {
        var userId = Guid.NewGuid();

        var result = await _service.ComputeStatusAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(MembershipStatus.None);
    }

    [HumansFact]
    public async Task ComputeStatusAsync_SuspendedProfile_ReturnsSuspended()
    {
        var userId = Guid.NewGuid();
        SeedProfile(userId, isApproved: true, isSuspended: true);

        var result = await _service.ComputeStatusAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(MembershipStatus.Suspended);
    }

    [HumansFact]
    public async Task ComputeStatusAsync_ApprovedWithActiveRole_NoExpiredConsents_ReturnsActive()
    {
        var userId = Guid.NewGuid();
        SeedProfile(userId, isApproved: true, isSuspended: false);
        SeedActiveRole(userId);
        SeedVolunteersTeamMember(userId);

        var result = await _service.ComputeStatusAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(MembershipStatus.Active);
    }

    [HumansFact]
    public async Task ComputeStatusAsync_ApprovedWithExpiredConsents_ReturnsInactive()
    {
        var userId = Guid.NewGuid();
        SeedProfile(userId, isApproved: true, isSuspended: false);
        SeedVolunteersTeamMember(userId);

        // Seed a required doc with grace=0 and effectiveFrom in the past (expired, not signed)
        SeedRequiredVersion(SystemTeamIds.Volunteers, Guid.NewGuid(), gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));

        var result = await _service.ComputeStatusAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(MembershipStatus.Inactive);
    }

    // --- HasActiveRolesAsync tests ---

    [HumansFact]
    public async Task HasActiveRolesAsync_ActiveRole_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        SeedActiveRole(userId);

        var result = await _service.HasActiveRolesAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task HasActiveRolesAsync_NoRoles_ReturnsFalse()
    {
        var userId = Guid.NewGuid();

        var result = await _service.HasActiveRolesAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    // --- HasAllRequiredConsentsAsync tests ---

    [HumansFact]
    public async Task HasAllRequiredConsentsAsync_AllSigned_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedRequiredVersion(SystemTeamIds.Volunteers, versionId);
        SeedConsent(userId, versionId);

        var result = await _service.HasAllRequiredConsentsAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task HasAllRequiredConsentsAsync_OneMissing_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        var v1 = Guid.NewGuid();
        var v2 = Guid.NewGuid();
        SeedRequiredVersion(SystemTeamIds.Volunteers, v1);
        SeedRequiredVersion(SystemTeamIds.Volunteers, v2);
        SeedConsent(userId, v1); // v2 unsigned

        var result = await _service.HasAllRequiredConsentsAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task HasAllRequiredConsentsAsync_NoRequiredDocs_ReturnsTrue()
    {
        var userId = Guid.NewGuid();

        var result = await _service.HasAllRequiredConsentsAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeTrue();
    }

    // --- HasAllRequiredConsentsForTeamAsync tests ---

    [HumansFact]
    public async Task HasAllRequiredConsentsForTeamAsync_AllSigned_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        var team = SeedTeam("Geeks", SystemTeamType.None);
        var versionId = Guid.NewGuid();
        SeedRequiredVersion(team.Id, versionId);
        SeedConsent(userId, versionId);

        var result = await _service.HasAllRequiredConsentsForTeamAsync(userId, team.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task HasAllRequiredConsentsForTeamAsync_OneMissing_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        var team = SeedTeam("Geeks", SystemTeamType.None);
        SeedRequiredVersion(team.Id, Guid.NewGuid()); // unsigned

        var result = await _service.HasAllRequiredConsentsForTeamAsync(userId, team.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task HasAllRequiredConsentsForTeamAsync_NoRequiredDocs_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        var team = SeedTeam("Geeks", SystemTeamType.None);

        var result = await _service.HasAllRequiredConsentsForTeamAsync(userId, team.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeTrue();
    }

    // --- HasAnyExpiredConsentsAsync tests ---

    [HumansFact]
    public async Task HasAnyExpiredConsentsAsync_ExpiredUnsigned_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        SeedRequiredVersion(SystemTeamIds.Volunteers, Guid.NewGuid(), gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));

        var result = await _service.HasAnyExpiredConsentsAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task HasAnyExpiredConsentsAsync_WithinGracePeriod_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        SeedRequiredVersion(SystemTeamIds.Volunteers, Guid.NewGuid(), gracePeriodDays: 365,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));

        var result = await _service.HasAnyExpiredConsentsAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task HasAnyExpiredConsentsAsync_AllSigned_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedRequiredVersion(SystemTeamIds.Volunteers, versionId, gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));
        SeedConsent(userId, versionId);

        var result = await _service.HasAnyExpiredConsentsAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    // --- HasAnyExpiredConsentsForTeamAsync tests ---

    [HumansFact]
    public async Task HasAnyExpiredConsentsForTeamAsync_ExpiredUnsigned_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        var team = SeedTeam("Geeks", SystemTeamType.None);
        SeedRequiredVersion(team.Id, Guid.NewGuid(), gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));

        var result = await _service.HasAnyExpiredConsentsForTeamAsync(userId, team.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task HasAnyExpiredConsentsForTeamAsync_WithinGracePeriod_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        var team = SeedTeam("Geeks", SystemTeamType.None);
        SeedRequiredVersion(team.Id, Guid.NewGuid(), gracePeriodDays: 365,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));

        var result = await _service.HasAnyExpiredConsentsForTeamAsync(userId, team.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task HasAnyExpiredConsentsForTeamAsync_AllSigned_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        var team = SeedTeam("Geeks", SystemTeamType.None);
        var versionId = Guid.NewGuid();
        SeedRequiredVersion(team.Id, versionId, gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));
        SeedConsent(userId, versionId);

        var result = await _service.HasAnyExpiredConsentsForTeamAsync(userId, team.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    // --- GetMissingConsentVersionsAsync tests ---

    [HumansFact]
    public async Task GetMissingConsentVersionsAsync_ReturnsMissingIds()
    {
        var userId = Guid.NewGuid();
        var v1 = Guid.NewGuid();
        var v2 = Guid.NewGuid();
        SeedRequiredVersion(SystemTeamIds.Volunteers, v1);
        SeedRequiredVersion(SystemTeamIds.Volunteers, v2);
        SeedConsent(userId, v1); // sign only v1

        var result = await _service.GetMissingConsentVersionsAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().ContainSingle().Which.Should().Be(v2);
    }

    [HumansFact]
    public async Task GetMissingConsentVersionsAsync_AllSigned_ReturnsEmpty()
    {
        var userId = Guid.NewGuid();
        var v1 = Guid.NewGuid();
        SeedRequiredVersion(SystemTeamIds.Volunteers, v1);
        SeedConsent(userId, v1);

        var result = await _service.GetMissingConsentVersionsAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetMissingConsentVersionsAsync_NoneSigned_ReturnsAll()
    {
        var userId = Guid.NewGuid();
        var v1 = Guid.NewGuid();
        var v2 = Guid.NewGuid();
        SeedRequiredVersion(SystemTeamIds.Volunteers, v1);
        SeedRequiredVersion(SystemTeamIds.Volunteers, v2);

        var result = await _service.GetMissingConsentVersionsAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().HaveCount(2);
        result.Should().Contain(v1);
        result.Should().Contain(v2);
    }

    // --- GetUsersRequiringStatusUpdateAsync tests ---

    [HumansFact]
    public async Task GetUsersRequiringStatusUpdateAsync_UsersWithActiveRolesAndExpiredConsents_ReturnsThem()
    {
        var userId = Guid.NewGuid();
        SeedActiveRole(userId);
        SeedActiveRoleInList(userId);
        SeedRequiredVersion(SystemTeamIds.Volunteers, Guid.NewGuid(), gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));

        var result = await _service.GetUsersRequiringStatusUpdateAsync(Xunit.TestContext.Current.CancellationToken);

        result.Should().Contain(userId);
    }

    [HumansFact]
    public async Task GetUsersRequiringStatusUpdateAsync_UsersWithoutActiveRoles_ExcludesThem()
    {
        var userId = Guid.NewGuid();
        // No active role registered → user not in GetUserIdsWithActiveAssignmentsAsync result
        SeedRequiredVersion(SystemTeamIds.Volunteers, Guid.NewGuid(), gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));

        var result = await _service.GetUsersRequiringStatusUpdateAsync(Xunit.TestContext.Current.CancellationToken);

        result.Should().NotContain(userId);
    }

    [HumansFact]
    public async Task GetUsersRequiringStatusUpdateAsync_NoExpiredConsents_ReturnsEmpty()
    {
        var userId = Guid.NewGuid();
        SeedActiveRole(userId);
        SeedActiveRoleInList(userId);
        // No required docs → no expired consents

        var result = await _service.GetUsersRequiringStatusUpdateAsync(Xunit.TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
    }

    // --- GetUsersWithAllRequiredConsentsAsync tests ---

    [HumansFact]
    public async Task GetUsersWithAllRequiredConsentsAsync_AllSigned_ReturnsUser()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedRequiredVersion(SystemTeamIds.Volunteers, versionId);
        SeedConsent(userId, versionId);

        var result = await _service.GetUsersWithAllRequiredConsentsAsync([userId], Xunit.TestContext.Current.CancellationToken);

        result.Should().Contain(userId);
    }

    [HumansFact]
    public async Task GetUsersWithAllRequiredConsentsAsync_MissingConsent_ExcludesUser()
    {
        var userId = Guid.NewGuid();
        SeedRequiredVersion(SystemTeamIds.Volunteers, Guid.NewGuid()); // unsigned

        var result = await _service.GetUsersWithAllRequiredConsentsAsync([userId], Xunit.TestContext.Current.CancellationToken);

        result.Should().NotContain(userId);
    }

    [HumansFact]
    public async Task GetUsersWithAllRequiredConsentsAsync_EmptyInput_ReturnsEmpty()
    {
        SeedRequiredVersion(SystemTeamIds.Volunteers, Guid.NewGuid());

        var result = await _service.GetUsersWithAllRequiredConsentsAsync([], Xunit.TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
    }

    // --- GetUsersWithAnyExpiredConsentsAsync tests ---

    [HumansFact]
    public async Task GetUsersWithAnyExpiredConsentsAsync_ExpiredUnsigned_ReturnsUser()
    {
        var userId = Guid.NewGuid();
        SeedRequiredVersion(SystemTeamIds.Volunteers, Guid.NewGuid(), gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));

        var result = await _service.GetUsersWithAnyExpiredConsentsAsync([userId], Xunit.TestContext.Current.CancellationToken);

        result.Should().Contain(userId);
    }

    [HumansFact]
    public async Task GetUsersWithAnyExpiredConsentsAsync_NoExpiredVersions_ReturnsEmpty()
    {
        var userId = Guid.NewGuid();
        // grace=365 → not expired yet
        SeedRequiredVersion(SystemTeamIds.Volunteers, Guid.NewGuid(), gracePeriodDays: 365,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));

        var result = await _service.GetUsersWithAnyExpiredConsentsAsync([userId], Xunit.TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetUsersWithAnyExpiredConsentsAsync_EmptyInput_ReturnsEmpty()
    {
        SeedRequiredVersion(SystemTeamIds.Volunteers, Guid.NewGuid(), gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));

        var result = await _service.GetUsersWithAnyExpiredConsentsAsync([], Xunit.TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
    }

    // --- Seed helpers ---

    /// <summary>
    /// Stand-ins for the <c>Team</c> / <c>TeamMember</c> entities the seed maps used to hold.
    /// Both turned internal to <c>Humans.Teams</c> at that section's G5; the substitutes only
    /// ever read the four members below, and keeping the property names identical leaves every
    /// call site in the test bodies unchanged (design §15 step 8, Campaigns' "rewrite the stub").
    /// </summary>
    private sealed record SeedTeamRow(Guid Id, string Name, SystemTeamType SystemTeamType);

    private sealed record SeedMembership(Guid TeamId, Guid UserId, TeamMemberRole Role, SeedTeamRow Team)
    {
        public Instant? LeftAt { get; init; }
    }

    private SeedTeamRow SeedTeam(string name, SystemTeamType systemType, Guid? id = null)
    {
        var team = new SeedTeamRow(id ?? Guid.NewGuid(), name, systemType);
        _teamsById[team.Id] = team;
        return team;
    }

    private void SeedTeamMember(Guid userId, Guid teamId, TeamMemberRole role)
    {
        if (!_teamsById.TryGetValue(teamId, out var team))
        {
            team = SeedTeam($"team-{teamId}", SystemTeamType.None, teamId);
        }
        var tm = new SeedMembership(teamId, userId, role, team);
        if (!_teamMembershipsByUserId.TryGetValue(userId, out var list))
        {
            list = [];
            _teamMembershipsByUserId[userId] = list;
        }
        list.Add(tm);
    }

    private void SeedVolunteersTeamMember(Guid userId)
    {
        if (!_teamsById.ContainsKey(SystemTeamIds.Volunteers))
        {
            SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        }
        SeedTeamMember(userId, SystemTeamIds.Volunteers, TeamMemberRole.Member);
    }

    private void SeedProfile(Guid userId, bool isApproved, bool isSuspended)
    {
        _profilesByUserId[userId] = UserFixtures.Profile(
            burnerName: "Tester",
            firstName: "Test",
            lastName: "User",
            isApproved: isApproved,
            createdAt: _clock.GetCurrentInstant());
        _statesByUserId[userId] = isSuspended ? UserState.Suspended : UserState.Active;
    }

    private void SeedActiveRole(Guid userId)
    {
        _membershipQuery.HasAnyActiveAssignmentAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
    }

    private readonly List<Guid> _activeRoleUserIds = [];

    private void SeedActiveRoleInList(Guid userId)
    {
        _activeRoleUserIds.Add(userId);
        _membershipQuery.GetUserIdsWithActiveAssignmentsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>(_activeRoleUserIds));
    }

    private void SeedConsent(Guid userId, Guid versionId)
    {
        if (!_consentedVersionsByUser.TryGetValue(userId, out var set))
        {
            set = [];
            _consentedVersionsByUser[userId] = set;
        }
        set.Add(versionId);
    }

    private void SeedRequiredVersion(Guid teamId, Guid versionId, int gracePeriodDays = 0, Instant? effectiveFrom = null)
    {
        var now = _clock.GetCurrentInstant();
        var docId = Guid.NewGuid();
        var version = new RequiredDocumentVersionSnapshot(
            Id: versionId,
            LegalDocumentId: docId,
            LegalDocumentName: $"Doc-{docId}",
            LegalDocumentGracePeriodDays: gracePeriodDays,
            VersionNumber: "v1",
            EffectiveFrom: effectiveFrom ?? now - Duration.FromDays(1),
            RequiresReConsent: false,
            ChangesSummary: null);
        if (!_requiredVersionsByTeam.TryGetValue(teamId, out var list))
        {
            list = [];
            _requiredVersionsByTeam[teamId] = list;
        }
        list.Add(version);
    }

    private UserState StateOf(Guid userId) =>
        _statesByUserId.TryGetValue(userId, out var state)
            ? state
            : UserFixtures.StateFor(_profilesByUserId.GetValueOrDefault(userId));

    private static UserInfo WrapInUserInfo(Guid userId, ProfileInfo profile, UserState state) => UserInfo.Create(
        user: new User
        {
            Id = userId,
            DisplayName = profile.BurnerName,
            PreferredLanguage = "en",
            CreatedAt = profile.CreatedAt,
            State = state,
        },
        userEmails: [],
        eventParticipations: [],
        externalLogins: [],
        profile: profile,
        communicationPreferences: []);
}

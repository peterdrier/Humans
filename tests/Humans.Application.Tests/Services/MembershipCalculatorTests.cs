using AwesomeAssertions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Services.Governance;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Xunit;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Legal;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Application.Tests.Services;

public class MembershipCalculatorTests
{
    private readonly FakeClock _clock;
    private readonly MembershipCalculator _service;
    private readonly IProfileService _profileService = Substitute.For<IProfileService>();
    private readonly IMembershipQuery _membershipQuery = Substitute.For<IMembershipQuery>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IConsentService _consentService = Substitute.For<IConsentService>();
    private readonly ILegalDocumentSyncService _legalDocumentSyncService = Substitute.For<ILegalDocumentSyncService>();

    // Seed backing state — section service substitutes read from these maps.
    private readonly Dictionary<Guid, Profile> _profilesByUserId = new();
    private readonly Dictionary<Guid, List<TeamMember>> _teamMembershipsByUserId = new();
    private readonly Dictionary<Guid, Team> _teamsById = new();
    private readonly Dictionary<Guid, List<DocumentVersion>> _requiredVersionsByTeam = new();
    private readonly Dictionary<Guid, HashSet<Guid>> _consentedVersionsByUser = new();

    public MembershipCalculatorTests()
    {
        _clock = new FakeClock(Instant.FromUtc(2026, 2, 15, 16, 0));

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IConsentService)).Returns(_consentService);

        _service = new MembershipCalculator(
            _profileService,
            _membershipQuery,
            _userService,
            _legalDocumentSyncService,
            serviceProvider,
            _clock);

        // Wire substitutes to the seed maps so tests can just mutate state.
        _profileService.GetProfileAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(_profilesByUserId.GetValueOrDefault(ci.Arg<Guid>())));

        _profileService.GetByUserIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ids = ci.Arg<IReadOnlyCollection<Guid>>();
                var map = ids
                    .Where(_profilesByUserId.ContainsKey)
                    .ToDictionary(id => id, id => _profilesByUserId[id]);
                return Task.FromResult<IReadOnlyDictionary<Guid, Profile>>(map);
            });

        _membershipQuery.GetUserTeamsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var userId = ci.Arg<Guid>();
                var memberships = _teamMembershipsByUserId.GetValueOrDefault(userId) ?? new();
                return Task.FromResult<IReadOnlyList<TeamMember>>(memberships);
            });

        _membershipQuery.IsUserMemberOfTeamAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var teamId = ci.ArgAt<Guid>(0);
                var userId = ci.ArgAt<Guid>(1);
                var memberships = _teamMembershipsByUserId.GetValueOrDefault(userId) ?? new();
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
                var versions = _requiredVersionsByTeam.GetValueOrDefault(teamId) ?? new();
                return Task.FromResult<IReadOnlyList<DocumentVersion>>(versions);
            });

        _consentService.GetConsentedVersionIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var userId = ci.Arg<Guid>();
                var set = _consentedVersionsByUser.GetValueOrDefault(userId) ?? new();
                return Task.FromResult<IReadOnlySet<Guid>>(set);
            });

        _consentService.GetConsentMapForUsersAsync(
            Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var userIds = ci.Arg<IReadOnlyList<Guid>>();
                var result = userIds.ToDictionary(
                    id => id,
                    id => (IReadOnlySet<Guid>)(_consentedVersionsByUser.GetValueOrDefault(id) ?? new HashSet<Guid>()));
                return Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>>(result);
            });
    }

    [HumansFact]
    public async Task ComputeStatusAsync_NotApprovedProfile_ReturnsPending()
    {
        var userId = Guid.NewGuid();
        SeedProfile(userId, isApproved: false, isSuspended: false);
        SeedActiveRole(userId);

        var result = await _service.ComputeStatusAsync(userId);

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

        var snapshot = await _service.GetMembershipSnapshotAsync(userId);

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

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId);

        result.Should().Contain(SystemTeamIds.Volunteers);
    }

    [HumansFact]
    public async Task GetRequiredTeamIdsForUserAsync_IncludesCoordinators_WhenUserIsCoordinatorOfUserCreatedTeam()
    {
        var userId = Guid.NewGuid();
        var userTeam = SeedTeam("Geeks", SystemTeamType.None);
        SeedTeamMember(userId, userTeam.Id, TeamMemberRole.Coordinator);

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId);

        result.Should().Contain(SystemTeamIds.Volunteers);
        result.Should().Contain(SystemTeamIds.Coordinators);
    }

    [HumansFact]
    public async Task GetRequiredTeamIdsForUserAsync_ExcludesCoordinators_WhenUserIsOnlyMember()
    {
        var userId = Guid.NewGuid();
        var userTeam = SeedTeam("Geeks", SystemTeamType.None);
        SeedTeamMember(userId, userTeam.Id, TeamMemberRole.Member);

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId);

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

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId);

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

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId);

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

        var snapshot = await _service.GetMembershipSnapshotAsync(userId);

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

        var snapshot = await _service.GetMembershipSnapshotAsync(userId);

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

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId);

        result.Should().Contain(SystemTeamIds.Volunteers);
        result.Should().Contain(SystemTeamIds.Colaboradors);
    }

    [HumansFact]
    public async Task GetRequiredTeamIdsForUserAsync_ExcludesColaboradors_WhenUserIsNotColaborador()
    {
        var userId = Guid.NewGuid();
        SeedVolunteersTeamMember(userId);

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId);

        result.Should().Contain(SystemTeamIds.Volunteers);
        result.Should().NotContain(SystemTeamIds.Colaboradors);
    }

    // --- ComputeStatusAsync (additional tests) ---

    [HumansFact]
    public async Task ComputeStatusAsync_NoProfile_ReturnsNone()
    {
        var userId = Guid.NewGuid();

        var result = await _service.ComputeStatusAsync(userId);

        result.Should().Be(MembershipStatus.None);
    }

    [HumansFact]
    public async Task ComputeStatusAsync_SuspendedProfile_ReturnsSuspended()
    {
        var userId = Guid.NewGuid();
        SeedProfile(userId, isApproved: true, isSuspended: true);

        var result = await _service.ComputeStatusAsync(userId);

        result.Should().Be(MembershipStatus.Suspended);
    }

    [HumansFact]
    public async Task ComputeStatusAsync_ApprovedWithActiveRole_NoExpiredConsents_ReturnsActive()
    {
        var userId = Guid.NewGuid();
        SeedProfile(userId, isApproved: true, isSuspended: false);
        SeedActiveRole(userId);
        SeedVolunteersTeamMember(userId);

        var result = await _service.ComputeStatusAsync(userId);

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

        var result = await _service.ComputeStatusAsync(userId);

        result.Should().Be(MembershipStatus.Inactive);
    }

    // --- HasActiveRolesAsync tests ---

    [HumansFact]
    public async Task HasActiveRolesAsync_ActiveRole_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        SeedActiveRole(userId);

        var result = await _service.HasActiveRolesAsync(userId);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task HasActiveRolesAsync_NoRoles_ReturnsFalse()
    {
        var userId = Guid.NewGuid();

        var result = await _service.HasActiveRolesAsync(userId);

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

        var result = await _service.HasAllRequiredConsentsAsync(userId);

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

        var result = await _service.HasAllRequiredConsentsAsync(userId);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task HasAllRequiredConsentsAsync_NoRequiredDocs_ReturnsTrue()
    {
        var userId = Guid.NewGuid();

        var result = await _service.HasAllRequiredConsentsAsync(userId);

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

        var result = await _service.HasAllRequiredConsentsForTeamAsync(userId, team.Id);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task HasAllRequiredConsentsForTeamAsync_OneMissing_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        var team = SeedTeam("Geeks", SystemTeamType.None);
        SeedRequiredVersion(team.Id, Guid.NewGuid()); // unsigned

        var result = await _service.HasAllRequiredConsentsForTeamAsync(userId, team.Id);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task HasAllRequiredConsentsForTeamAsync_NoRequiredDocs_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        var team = SeedTeam("Geeks", SystemTeamType.None);

        var result = await _service.HasAllRequiredConsentsForTeamAsync(userId, team.Id);

        result.Should().BeTrue();
    }

    // --- HasAnyExpiredConsentsAsync tests ---

    [HumansFact]
    public async Task HasAnyExpiredConsentsAsync_ExpiredUnsigned_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        SeedRequiredVersion(SystemTeamIds.Volunteers, Guid.NewGuid(), gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));

        var result = await _service.HasAnyExpiredConsentsAsync(userId);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task HasAnyExpiredConsentsAsync_WithinGracePeriod_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        SeedRequiredVersion(SystemTeamIds.Volunteers, Guid.NewGuid(), gracePeriodDays: 365,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));

        var result = await _service.HasAnyExpiredConsentsAsync(userId);

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

        var result = await _service.HasAnyExpiredConsentsAsync(userId);

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

        var result = await _service.HasAnyExpiredConsentsForTeamAsync(userId, team.Id);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task HasAnyExpiredConsentsForTeamAsync_WithinGracePeriod_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        var team = SeedTeam("Geeks", SystemTeamType.None);
        SeedRequiredVersion(team.Id, Guid.NewGuid(), gracePeriodDays: 365,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));

        var result = await _service.HasAnyExpiredConsentsForTeamAsync(userId, team.Id);

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

        var result = await _service.HasAnyExpiredConsentsForTeamAsync(userId, team.Id);

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

        var result = await _service.GetMissingConsentVersionsAsync(userId);

        result.Should().ContainSingle().Which.Should().Be(v2);
    }

    [HumansFact]
    public async Task GetMissingConsentVersionsAsync_AllSigned_ReturnsEmpty()
    {
        var userId = Guid.NewGuid();
        var v1 = Guid.NewGuid();
        SeedRequiredVersion(SystemTeamIds.Volunteers, v1);
        SeedConsent(userId, v1);

        var result = await _service.GetMissingConsentVersionsAsync(userId);

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

        var result = await _service.GetMissingConsentVersionsAsync(userId);

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

        var result = await _service.GetUsersRequiringStatusUpdateAsync();

        result.Should().Contain(userId);
    }

    [HumansFact]
    public async Task GetUsersRequiringStatusUpdateAsync_UsersWithoutActiveRoles_ExcludesThem()
    {
        var userId = Guid.NewGuid();
        // No active role registered → user not in GetUserIdsWithActiveAssignmentsAsync result
        SeedRequiredVersion(SystemTeamIds.Volunteers, Guid.NewGuid(), gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));

        var result = await _service.GetUsersRequiringStatusUpdateAsync();

        result.Should().NotContain(userId);
    }

    [HumansFact]
    public async Task GetUsersRequiringStatusUpdateAsync_NoExpiredConsents_ReturnsEmpty()
    {
        var userId = Guid.NewGuid();
        SeedActiveRole(userId);
        SeedActiveRoleInList(userId);
        // No required docs → no expired consents

        var result = await _service.GetUsersRequiringStatusUpdateAsync();

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

        var result = await _service.GetUsersWithAllRequiredConsentsAsync(new[] { userId });

        result.Should().Contain(userId);
    }

    [HumansFact]
    public async Task GetUsersWithAllRequiredConsentsAsync_MissingConsent_ExcludesUser()
    {
        var userId = Guid.NewGuid();
        SeedRequiredVersion(SystemTeamIds.Volunteers, Guid.NewGuid()); // unsigned

        var result = await _service.GetUsersWithAllRequiredConsentsAsync(new[] { userId });

        result.Should().NotContain(userId);
    }

    [HumansFact]
    public async Task GetUsersWithAllRequiredConsentsAsync_EmptyInput_ReturnsEmpty()
    {
        SeedRequiredVersion(SystemTeamIds.Volunteers, Guid.NewGuid());

        var result = await _service.GetUsersWithAllRequiredConsentsAsync(Array.Empty<Guid>());

        result.Should().BeEmpty();
    }

    // --- GetUsersWithAnyExpiredConsentsAsync tests ---

    [HumansFact]
    public async Task GetUsersWithAnyExpiredConsentsAsync_ExpiredUnsigned_ReturnsUser()
    {
        var userId = Guid.NewGuid();
        SeedRequiredVersion(SystemTeamIds.Volunteers, Guid.NewGuid(), gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));

        var result = await _service.GetUsersWithAnyExpiredConsentsAsync(new[] { userId });

        result.Should().Contain(userId);
    }

    [HumansFact]
    public async Task GetUsersWithAnyExpiredConsentsAsync_NoExpiredVersions_ReturnsEmpty()
    {
        var userId = Guid.NewGuid();
        // grace=365 → not expired yet
        SeedRequiredVersion(SystemTeamIds.Volunteers, Guid.NewGuid(), gracePeriodDays: 365,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));

        var result = await _service.GetUsersWithAnyExpiredConsentsAsync(new[] { userId });

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetUsersWithAnyExpiredConsentsAsync_EmptyInput_ReturnsEmpty()
    {
        SeedRequiredVersion(SystemTeamIds.Volunteers, Guid.NewGuid(), gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));

        var result = await _service.GetUsersWithAnyExpiredConsentsAsync(Array.Empty<Guid>());

        result.Should().BeEmpty();
    }

    // --- Seed helpers ---

    private Team SeedTeam(string name, SystemTeamType systemType, Guid? id = null)
    {
        var team = new Team
        {
            Id = id ?? Guid.NewGuid(),
            Name = name,
            Slug = name.ToLowerInvariant(),
            SystemTeamType = systemType,
            IsActive = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _teamsById[team.Id] = team;
        return team;
    }

    private void SeedTeamMember(Guid userId, Guid teamId, TeamMemberRole role)
    {
        if (!_teamsById.TryGetValue(teamId, out var team))
        {
            team = SeedTeam($"team-{teamId}", SystemTeamType.None, teamId);
        }
        var tm = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Role = role,
            JoinedAt = _clock.GetCurrentInstant(),
            Team = team
        };
        if (!_teamMembershipsByUserId.TryGetValue(userId, out var list))
        {
            list = new List<TeamMember>();
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
        _profilesByUserId[userId] = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = "Tester",
            FirstName = "Test",
            LastName = "User",
            IsApproved = isApproved,
            IsSuspended = isSuspended,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
    }

    private void SeedActiveRole(Guid userId)
    {
        _membershipQuery.HasAnyActiveAssignmentAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
    }

    private readonly List<Guid> _activeRoleUserIds = new();

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
            set = new HashSet<Guid>();
            _consentedVersionsByUser[userId] = set;
        }
        set.Add(versionId);
    }

    private void SeedRequiredVersion(Guid teamId, Guid versionId, int gracePeriodDays = 0, Instant? effectiveFrom = null)
    {
        var now = _clock.GetCurrentInstant();
        var docId = Guid.NewGuid();
        var doc = new LegalDocument
        {
            Id = docId,
            Name = $"Doc-{docId}",
            TeamId = teamId,
            IsRequired = true,
            IsActive = true,
            GracePeriodDays = gracePeriodDays,
            CurrentCommitSha = "test",
            CreatedAt = now,
            LastSyncedAt = now
        };
        var version = new DocumentVersion
        {
            Id = versionId,
            LegalDocumentId = docId,
            VersionNumber = "v1",
            CommitSha = "abc123",
            EffectiveFrom = effectiveFrom ?? now - Duration.FromDays(1),
            RequiresReConsent = false,
            CreatedAt = now,
            LegalDocument = doc
        };
        if (!_requiredVersionsByTeam.TryGetValue(teamId, out var list))
        {
            list = new List<DocumentVersion>();
            _requiredVersionsByTeam[teamId] = list;
        }
        list.Add(version);
    }
}

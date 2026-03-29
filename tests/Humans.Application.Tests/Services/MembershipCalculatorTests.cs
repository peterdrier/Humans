using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Application.Tests.Services;

public class MembershipCalculatorTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly MembershipCalculator _service;

    public MembershipCalculatorTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 2, 15, 16, 0));
        _service = new MembershipCalculator(_dbContext, _clock);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ComputeStatusAsync_NotApprovedProfile_ReturnsPending()
    {
        var userId = Guid.NewGuid();
        await SeedProfileAsync(userId, isApproved: false, isSuspended: false);
        await SeedActiveRoleAsync(userId, "Board");

        var result = await _service.ComputeStatusAsync(userId);

        result.Should().Be(MembershipStatus.Pending);
    }

    [Fact]
    public async Task GetMembershipSnapshotAsync_ReturnsConsolidatedState()
    {
        var userId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        var docId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        await SeedProfileAsync(userId, isApproved: true, isSuspended: false);
        await SeedActiveRoleAsync(userId, "Board");

        _dbContext.LegalDocuments.Add(new LegalDocument
        {
            Id = docId,
            Name = "Privacy Policy",
            TeamId = SystemTeamIds.Volunteers,
            IsRequired = true,
            IsActive = true,
            GracePeriodDays = 0,
            CurrentCommitSha = "test",
            CreatedAt = now,
            LastSyncedAt = now
        });

        _dbContext.DocumentVersions.Add(new DocumentVersion
        {
            Id = versionId,
            LegalDocumentId = docId,
            VersionNumber = "v1",
            CommitSha = "abc123",
            EffectiveFrom = now - Duration.FromDays(1),
            RequiresReConsent = false,
            CreatedAt = now,
            LegalDocument = _dbContext.LegalDocuments.Local.First()
        });

        _dbContext.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = SystemTeamIds.Volunteers,
            UserId = userId,
            Role = TeamMemberRole.Member,
            JoinedAt = now
        });

        await _dbContext.SaveChangesAsync();

        var snapshot = await _service.GetMembershipSnapshotAsync(userId);

        snapshot.RequiredConsentCount.Should().Be(1);
        snapshot.PendingConsentCount.Should().Be(1);
        snapshot.MissingConsentVersionIds.Should().ContainSingle().Which.Should().Be(versionId);
        snapshot.IsVolunteerMember.Should().BeTrue();
        snapshot.Status.Should().Be(MembershipStatus.Inactive);
    }

    // --- GetRequiredTeamIdsForUserAsync tests ---

    [Fact]
    public async Task GetRequiredTeamIdsForUserAsync_AlwaysIncludesVolunteers()
    {
        var userId = Guid.NewGuid();

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId);

        result.Should().Contain(SystemTeamIds.Volunteers);
    }

    [Fact]
    public async Task GetRequiredTeamIdsForUserAsync_IncludesCoordinators_WhenUserIsCoordinatorOfUserCreatedTeam()
    {
        var userId = Guid.NewGuid();
        var userTeam = SeedTeam("Geeks", SystemTeamType.None);
        SeedTeamMember(userTeam.Id, userId, TeamMemberRole.Coordinator);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId);

        result.Should().Contain(SystemTeamIds.Volunteers);
        result.Should().Contain(SystemTeamIds.Coordinators);
    }

    [Fact]
    public async Task GetRequiredTeamIdsForUserAsync_ExcludesCoordinators_WhenUserIsOnlyMember()
    {
        var userId = Guid.NewGuid();
        var userTeam = SeedTeam("Geeks", SystemTeamType.None);
        SeedTeamMember(userTeam.Id, userId, TeamMemberRole.Member);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId);

        result.Should().Contain(SystemTeamIds.Volunteers);
        result.Should().NotContain(SystemTeamIds.Coordinators);
    }

    [Fact]
    public async Task GetRequiredTeamIdsForUserAsync_ExcludesCoordinators_WhenUserIsCoordinatorOfSystemTeam()
    {
        var userId = Guid.NewGuid();
        // Coordinator of the Volunteers system team should NOT trigger Coordinators eligibility
        var volunteersTeam = SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        SeedTeamMember(volunteersTeam.Id, userId, TeamMemberRole.Coordinator);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId);

        result.Should().Contain(SystemTeamIds.Volunteers);
        result.Should().NotContain(SystemTeamIds.Coordinators);
    }

    [Fact]
    public async Task GetRequiredTeamIdsForUserAsync_ExcludesCoordinators_WhenCoordinatorMembershipEnded()
    {
        var userId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        var userTeam = SeedTeam("Geeks", SystemTeamType.None);

        _dbContext.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = userTeam.Id,
            UserId = userId,
            Role = TeamMemberRole.Coordinator,
            JoinedAt = now - Duration.FromDays(30),
            LeftAt = now - Duration.FromDays(1) // Left the team
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId);

        result.Should().Contain(SystemTeamIds.Volunteers);
        result.Should().NotContain(SystemTeamIds.Coordinators);
    }

    [Fact]
    public async Task GetRequiredTeamIdsForUserAsync_IncludesCurrentTeamMemberships()
    {
        var userId = Guid.NewGuid();
        var geeks = SeedTeam("Geeks", SystemTeamType.None);
        var volunteers = SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        SeedTeamMember(geeks.Id, userId, TeamMemberRole.Member);
        SeedTeamMember(volunteers.Id, userId, TeamMemberRole.Member);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId);

        result.Should().Contain(geeks.Id);
        result.Should().Contain(SystemTeamIds.Volunteers);
    }

    // --- GetMembershipSnapshotAsync with Coordinators docs ---

    [Fact]
    public async Task GetMembershipSnapshotAsync_IncludesCoordinatorsDocsForCoordinatorUser()
    {
        var userId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();

        await SeedProfileAsync(userId, isApproved: true, isSuspended: false);
        await SeedActiveRoleAsync(userId, "Board");

        // System teams
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        SeedTeam("Coordinators", SystemTeamType.Coordinators, SystemTeamIds.Coordinators);

        // User-created team where user is Coordinator
        var geeks = SeedTeam("Geeks", SystemTeamType.None);
        SeedTeamMember(geeks.Id, userId, TeamMemberRole.Coordinator);

        // Volunteers member
        SeedTeamMember(SystemTeamIds.Volunteers, userId, TeamMemberRole.Member);

        // Volunteer doc (required)
        var volDocId = Guid.NewGuid();
        var volVersionId = Guid.NewGuid();
        _dbContext.LegalDocuments.Add(new LegalDocument
        {
            Id = volDocId,
            Name = "Privacy Policy",
            TeamId = SystemTeamIds.Volunteers,
            IsRequired = true,
            IsActive = true,
            GracePeriodDays = 0,
            CurrentCommitSha = "test",
            CreatedAt = now,
            LastSyncedAt = now
        });
        _dbContext.DocumentVersions.Add(new DocumentVersion
        {
            Id = volVersionId,
            LegalDocumentId = volDocId,
            VersionNumber = "v1",
            CommitSha = "abc123",
            EffectiveFrom = now - Duration.FromDays(1),
            RequiresReConsent = false,
            CreatedAt = now,
            LegalDocument = _dbContext.LegalDocuments.Local.First(d => d.Id == volDocId)
        });

        // Coordinators doc (required)
        var coordsDocId = Guid.NewGuid();
        var coordsVersionId = Guid.NewGuid();
        _dbContext.LegalDocuments.Add(new LegalDocument
        {
            Id = coordsDocId,
            Name = "Coordinator Agreement",
            TeamId = SystemTeamIds.Coordinators,
            IsRequired = true,
            IsActive = true,
            GracePeriodDays = 0,
            CurrentCommitSha = "test2",
            CreatedAt = now,
            LastSyncedAt = now
        });
        _dbContext.DocumentVersions.Add(new DocumentVersion
        {
            Id = coordsVersionId,
            LegalDocumentId = coordsDocId,
            VersionNumber = "v1",
            CommitSha = "def456",
            EffectiveFrom = now - Duration.FromDays(1),
            RequiresReConsent = false,
            CreatedAt = now,
            LegalDocument = _dbContext.LegalDocuments.Local.First(d => d.Id == coordsDocId)
        });

        await _dbContext.SaveChangesAsync();

        var snapshot = await _service.GetMembershipSnapshotAsync(userId);

        // Should include both Volunteers and Coordinators docs
        snapshot.RequiredConsentCount.Should().Be(2);
        snapshot.PendingConsentCount.Should().Be(2);
        snapshot.MissingConsentVersionIds.Should().Contain(volVersionId);
        snapshot.MissingConsentVersionIds.Should().Contain(coordsVersionId);
    }

    [Fact]
    public async Task GetMembershipSnapshotAsync_ExcludesCoordinatorsDocs_WhenUserIsNotCoordinator()
    {
        var userId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();

        await SeedProfileAsync(userId, isApproved: true, isSuspended: false);
        await SeedActiveRoleAsync(userId, "Board");

        // System teams
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        SeedTeam("Coordinators", SystemTeamType.Coordinators, SystemTeamIds.Coordinators);

        // User is just a member of a user-created team, not a coordinator
        var geeks = SeedTeam("Geeks", SystemTeamType.None);
        SeedTeamMember(geeks.Id, userId, TeamMemberRole.Member);
        SeedTeamMember(SystemTeamIds.Volunteers, userId, TeamMemberRole.Member);

        // Volunteer doc
        var volDocId = Guid.NewGuid();
        var volVersionId = Guid.NewGuid();
        _dbContext.LegalDocuments.Add(new LegalDocument
        {
            Id = volDocId,
            Name = "Privacy Policy",
            TeamId = SystemTeamIds.Volunteers,
            IsRequired = true,
            IsActive = true,
            GracePeriodDays = 0,
            CurrentCommitSha = "test",
            CreatedAt = now,
            LastSyncedAt = now
        });
        _dbContext.DocumentVersions.Add(new DocumentVersion
        {
            Id = volVersionId,
            LegalDocumentId = volDocId,
            VersionNumber = "v1",
            CommitSha = "abc123",
            EffectiveFrom = now - Duration.FromDays(1),
            RequiresReConsent = false,
            CreatedAt = now,
            LegalDocument = _dbContext.LegalDocuments.Local.First(d => d.Id == volDocId)
        });

        // Coordinators doc exists but should NOT appear for non-coordinators
        var coordsDocId = Guid.NewGuid();
        _dbContext.LegalDocuments.Add(new LegalDocument
        {
            Id = coordsDocId,
            Name = "Coordinator Agreement",
            TeamId = SystemTeamIds.Coordinators,
            IsRequired = true,
            IsActive = true,
            GracePeriodDays = 0,
            CurrentCommitSha = "test2",
            CreatedAt = now,
            LastSyncedAt = now
        });
        _dbContext.DocumentVersions.Add(new DocumentVersion
        {
            Id = Guid.NewGuid(),
            LegalDocumentId = coordsDocId,
            VersionNumber = "v1",
            CommitSha = "def456",
            EffectiveFrom = now - Duration.FromDays(1),
            RequiresReConsent = false,
            CreatedAt = now,
            LegalDocument = _dbContext.LegalDocuments.Local.First(d => d.Id == coordsDocId)
        });

        await _dbContext.SaveChangesAsync();

        var snapshot = await _service.GetMembershipSnapshotAsync(userId);

        // Should only include Volunteers doc, not Coordinators
        snapshot.RequiredConsentCount.Should().Be(1);
        snapshot.PendingConsentCount.Should().Be(1);
        snapshot.MissingConsentVersionIds.Should().ContainSingle().Which.Should().Be(volVersionId);
    }

    // --- GetRequiredTeamIdsForUserAsync: Colaboradors team ---

    [Fact]
    public async Task GetRequiredTeamIdsForUserAsync_IncludesColaboradors_WhenUserIsColaborador()
    {
        var userId = Guid.NewGuid();
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        SeedTeam("Colaboradors", SystemTeamType.Colaboradors, SystemTeamIds.Colaboradors);
        SeedTeamMember(SystemTeamIds.Volunteers, userId, TeamMemberRole.Member);
        SeedTeamMember(SystemTeamIds.Colaboradors, userId, TeamMemberRole.Member);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId);

        result.Should().Contain(SystemTeamIds.Volunteers);
        result.Should().Contain(SystemTeamIds.Colaboradors);
    }

    [Fact]
    public async Task GetRequiredTeamIdsForUserAsync_ExcludesColaboradors_WhenUserIsNotColaborador()
    {
        var userId = Guid.NewGuid();
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        SeedTeam("Colaboradors", SystemTeamType.Colaboradors, SystemTeamIds.Colaboradors);
        SeedTeamMember(SystemTeamIds.Volunteers, userId, TeamMemberRole.Member);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId);

        result.Should().Contain(SystemTeamIds.Volunteers);
        result.Should().NotContain(SystemTeamIds.Colaboradors);
    }

    // --- ComputeStatusAsync (additional tests) ---

    [Fact]
    public async Task ComputeStatusAsync_NoProfile_ReturnsNone()
    {
        var userId = Guid.NewGuid();

        var result = await _service.ComputeStatusAsync(userId);

        result.Should().Be(MembershipStatus.None);
    }

    [Fact]
    public async Task ComputeStatusAsync_SuspendedProfile_ReturnsSuspended()
    {
        var userId = Guid.NewGuid();
        await SeedProfileAsync(userId, isApproved: true, isSuspended: true);

        var result = await _service.ComputeStatusAsync(userId);

        result.Should().Be(MembershipStatus.Suspended);
    }

    [Fact]
    public async Task ComputeStatusAsync_ApprovedWithActiveRole_NoExpiredConsents_ReturnsActive()
    {
        var userId = Guid.NewGuid();
        await SeedProfileAsync(userId, isApproved: true, isSuspended: false);
        await SeedActiveRoleAsync(userId, "Board");
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        SeedTeamMember(SystemTeamIds.Volunteers, userId, TeamMemberRole.Member);
        await _dbContext.SaveChangesAsync();

        var result = await _service.ComputeStatusAsync(userId);

        result.Should().Be(MembershipStatus.Active);
    }

    [Fact]
    public async Task ComputeStatusAsync_ApprovedWithExpiredConsents_ReturnsInactive()
    {
        var userId = Guid.NewGuid();
        await SeedProfileAsync(userId, isApproved: true, isSuspended: false);
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        SeedTeamMember(SystemTeamIds.Volunteers, userId, TeamMemberRole.Member);
        // Seed a required doc with grace=0 and effectiveFrom in the past (expired, not signed)
        SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers, gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));
        await _dbContext.SaveChangesAsync();

        var result = await _service.ComputeStatusAsync(userId);

        result.Should().Be(MembershipStatus.Inactive);
    }

    // --- HasActiveRolesAsync tests ---

    [Fact]
    public async Task HasActiveRolesAsync_ActiveRole_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        await SeedActiveRoleAsync(userId, "Board");

        var result = await _service.HasActiveRolesAsync(userId);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasActiveRolesAsync_NoRoles_ReturnsFalse()
    {
        var userId = Guid.NewGuid();

        var result = await _service.HasActiveRolesAsync(userId);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasActiveRolesAsync_ExpiredRole_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        _dbContext.RoleAssignments.Add(new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleName = "Board",
            ValidFrom = now - Duration.FromDays(30),
            ValidTo = now - Duration.FromDays(1), // expired
            CreatedAt = now,
            CreatedByUserId = Guid.NewGuid()
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.HasActiveRolesAsync(userId);

        result.Should().BeFalse();
    }

    // --- HasAllRequiredConsentsAsync tests ---

    [Fact]
    public async Task HasAllRequiredConsentsAsync_AllSigned_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        var versionId = SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers);
        SeedConsentRecord(userId, versionId);
        await _dbContext.SaveChangesAsync();

        var result = await _service.HasAllRequiredConsentsAsync(userId);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasAllRequiredConsentsAsync_OneMissing_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        var v1 = SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers);
        SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers); // second doc, unsigned
        SeedConsentRecord(userId, v1);
        await _dbContext.SaveChangesAsync();

        var result = await _service.HasAllRequiredConsentsAsync(userId);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasAllRequiredConsentsAsync_NoRequiredDocs_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        await _dbContext.SaveChangesAsync();

        var result = await _service.HasAllRequiredConsentsAsync(userId);

        result.Should().BeTrue();
    }

    // --- HasAllRequiredConsentsForTeamAsync tests ---

    [Fact]
    public async Task HasAllRequiredConsentsForTeamAsync_AllSigned_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        var team = SeedTeam("Geeks", SystemTeamType.None);
        var versionId = SeedLegalDocumentWithVersion(team.Id);
        SeedConsentRecord(userId, versionId);
        await _dbContext.SaveChangesAsync();

        var result = await _service.HasAllRequiredConsentsForTeamAsync(userId, team.Id);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasAllRequiredConsentsForTeamAsync_OneMissing_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        var team = SeedTeam("Geeks", SystemTeamType.None);
        SeedLegalDocumentWithVersion(team.Id); // unsigned
        await _dbContext.SaveChangesAsync();

        var result = await _service.HasAllRequiredConsentsForTeamAsync(userId, team.Id);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasAllRequiredConsentsForTeamAsync_NoRequiredDocs_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        var team = SeedTeam("Geeks", SystemTeamType.None);
        await _dbContext.SaveChangesAsync();

        var result = await _service.HasAllRequiredConsentsForTeamAsync(userId, team.Id);

        result.Should().BeTrue();
    }

    // --- HasAnyExpiredConsentsAsync tests ---

    [Fact]
    public async Task HasAnyExpiredConsentsAsync_ExpiredUnsigned_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        // grace=0, effectiveFrom 10 days ago → expired
        SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers, gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));
        await _dbContext.SaveChangesAsync();

        var result = await _service.HasAnyExpiredConsentsAsync(userId);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasAnyExpiredConsentsAsync_WithinGracePeriod_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        // grace=365, effectiveFrom 10 days ago → effectiveFrom + 365 > now, still within grace
        SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers, gracePeriodDays: 365,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));
        await _dbContext.SaveChangesAsync();

        var result = await _service.HasAnyExpiredConsentsAsync(userId);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasAnyExpiredConsentsAsync_AllSigned_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        var versionId = SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers, gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));
        SeedConsentRecord(userId, versionId);
        await _dbContext.SaveChangesAsync();

        var result = await _service.HasAnyExpiredConsentsAsync(userId);

        result.Should().BeFalse();
    }

    // --- HasAnyExpiredConsentsForTeamAsync tests ---

    [Fact]
    public async Task HasAnyExpiredConsentsForTeamAsync_ExpiredUnsigned_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        var team = SeedTeam("Geeks", SystemTeamType.None);
        SeedLegalDocumentWithVersion(team.Id, gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));
        await _dbContext.SaveChangesAsync();

        var result = await _service.HasAnyExpiredConsentsForTeamAsync(userId, team.Id);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasAnyExpiredConsentsForTeamAsync_WithinGracePeriod_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        var team = SeedTeam("Geeks", SystemTeamType.None);
        SeedLegalDocumentWithVersion(team.Id, gracePeriodDays: 365,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));
        await _dbContext.SaveChangesAsync();

        var result = await _service.HasAnyExpiredConsentsForTeamAsync(userId, team.Id);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasAnyExpiredConsentsForTeamAsync_AllSigned_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        var team = SeedTeam("Geeks", SystemTeamType.None);
        var versionId = SeedLegalDocumentWithVersion(team.Id, gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));
        SeedConsentRecord(userId, versionId);
        await _dbContext.SaveChangesAsync();

        var result = await _service.HasAnyExpiredConsentsForTeamAsync(userId, team.Id);

        result.Should().BeFalse();
    }

    // --- GetMissingConsentVersionsAsync tests ---

    [Fact]
    public async Task GetMissingConsentVersionsAsync_ReturnsMissingIds()
    {
        var userId = Guid.NewGuid();
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        var v1 = SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers);
        var v2 = SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers);
        SeedConsentRecord(userId, v1); // sign only v1
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetMissingConsentVersionsAsync(userId);

        result.Should().ContainSingle().Which.Should().Be(v2);
    }

    [Fact]
    public async Task GetMissingConsentVersionsAsync_AllSigned_ReturnsEmpty()
    {
        var userId = Guid.NewGuid();
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        var v1 = SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers);
        SeedConsentRecord(userId, v1);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetMissingConsentVersionsAsync(userId);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMissingConsentVersionsAsync_NoneSigned_ReturnsAll()
    {
        var userId = Guid.NewGuid();
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        var v1 = SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers);
        var v2 = SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetMissingConsentVersionsAsync(userId);

        result.Should().HaveCount(2);
        result.Should().Contain(v1);
        result.Should().Contain(v2);
    }

    // --- GetUsersRequiringStatusUpdateAsync tests ---

    [Fact]
    public async Task GetUsersRequiringStatusUpdateAsync_UsersWithActiveRolesAndExpiredConsents_ReturnsThem()
    {
        var userId = Guid.NewGuid();
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        await SeedActiveRoleAsync(userId, "Board");
        SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers, gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUsersRequiringStatusUpdateAsync();

        result.Should().Contain(userId);
    }

    [Fact]
    public async Task GetUsersRequiringStatusUpdateAsync_UsersWithoutActiveRoles_ExcludesThem()
    {
        var userId = Guid.NewGuid();
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        // No active role, just an expired consent
        SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers, gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUsersRequiringStatusUpdateAsync();

        result.Should().NotContain(userId);
    }

    [Fact]
    public async Task GetUsersRequiringStatusUpdateAsync_NoExpiredConsents_ReturnsEmpty()
    {
        var userId = Guid.NewGuid();
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        await SeedActiveRoleAsync(userId, "Board");
        // No required docs → no expired consents
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUsersRequiringStatusUpdateAsync();

        result.Should().BeEmpty();
    }

    // --- GetUsersWithAllRequiredConsentsAsync tests ---

    [Fact]
    public async Task GetUsersWithAllRequiredConsentsAsync_AllSigned_ReturnsUser()
    {
        var userId = Guid.NewGuid();
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        var versionId = SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers);
        SeedConsentRecord(userId, versionId);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUsersWithAllRequiredConsentsAsync(new[] { userId });

        result.Should().Contain(userId);
    }

    [Fact]
    public async Task GetUsersWithAllRequiredConsentsAsync_MissingConsent_ExcludesUser()
    {
        var userId = Guid.NewGuid();
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers); // unsigned
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUsersWithAllRequiredConsentsAsync(new[] { userId });

        result.Should().NotContain(userId);
    }

    [Fact]
    public async Task GetUsersWithAllRequiredConsentsAsync_EmptyInput_ReturnsEmpty()
    {
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUsersWithAllRequiredConsentsAsync(Array.Empty<Guid>());

        result.Should().BeEmpty();
    }

    // --- GetUsersWithAnyExpiredConsentsAsync tests ---

    [Fact]
    public async Task GetUsersWithAnyExpiredConsentsAsync_ExpiredUnsigned_ReturnsUser()
    {
        var userId = Guid.NewGuid();
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers, gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUsersWithAnyExpiredConsentsAsync(new[] { userId });

        result.Should().Contain(userId);
    }

    [Fact]
    public async Task GetUsersWithAnyExpiredConsentsAsync_NoExpiredVersions_ReturnsEmpty()
    {
        var userId = Guid.NewGuid();
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        // grace=365 → not expired yet
        SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers, gracePeriodDays: 365,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUsersWithAnyExpiredConsentsAsync(new[] { userId });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUsersWithAnyExpiredConsentsAsync_EmptyInput_ReturnsEmpty()
    {
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers, gracePeriodDays: 0,
            effectiveFrom: _clock.GetCurrentInstant() - Duration.FromDays(10));
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUsersWithAnyExpiredConsentsAsync(Array.Empty<Guid>());

        result.Should().BeEmpty();
    }

    // --- Helpers ---

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
        _dbContext.Teams.Add(team);
        return team;
    }

    private void SeedTeamMember(Guid teamId, Guid userId, TeamMemberRole role)
    {
        _dbContext.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Role = role,
            JoinedAt = _clock.GetCurrentInstant()
        });
    }

    private async Task SeedProfileAsync(Guid userId, bool isApproved, bool isSuspended)
    {
        _dbContext.Profiles.Add(new Profile
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
        });

        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedActiveRoleAsync(Guid userId, string roleName)
    {
        _dbContext.RoleAssignments.Add(new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleName = roleName,
            ValidFrom = _clock.GetCurrentInstant() - Duration.FromDays(1),
            ValidTo = null,
            CreatedAt = _clock.GetCurrentInstant(),
            CreatedByUserId = Guid.NewGuid()
        });

        await _dbContext.SaveChangesAsync();
    }

    private void SeedConsentRecord(Guid userId, Guid versionId)
    {
        _dbContext.ConsentRecords.Add(new ConsentRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DocumentVersionId = versionId,
            ExplicitConsent = true,
            ConsentedAt = _clock.GetCurrentInstant(),
            IpAddress = "127.0.0.1",
            UserAgent = "test",
            ContentHash = "testhash"
        });
    }

    private Guid SeedLegalDocumentWithVersion(Guid teamId, Guid? docId = null, Guid? versionId = null, int gracePeriodDays = 0, Instant? effectiveFrom = null)
    {
        var now = _clock.GetCurrentInstant();
        var dId = docId ?? Guid.NewGuid();
        var vId = versionId ?? Guid.NewGuid();
        var doc = new LegalDocument
        {
            Id = dId,
            Name = $"Doc-{dId}",
            TeamId = teamId,
            IsRequired = true,
            IsActive = true,
            GracePeriodDays = gracePeriodDays,
            CurrentCommitSha = "test",
            CreatedAt = now,
            LastSyncedAt = now
        };
        _dbContext.LegalDocuments.Add(doc);
        _dbContext.DocumentVersions.Add(new DocumentVersion
        {
            Id = vId,
            LegalDocumentId = dId,
            VersionNumber = "v1",
            CommitSha = "abc123",
            EffectiveFrom = effectiveFrom ?? now - Duration.FromDays(1),
            RequiresReConsent = false,
            CreatedAt = now,
            LegalDocument = doc
        });
        return vId;
    }
}

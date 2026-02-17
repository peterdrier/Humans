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
    public async Task GetRequiredTeamIdsForUserAsync_IncludesLeads_WhenUserIsLeadOfUserCreatedTeam()
    {
        var userId = Guid.NewGuid();
        var userTeam = SeedTeam("Geeks", SystemTeamType.None);
        SeedTeamMember(userTeam.Id, userId, TeamMemberRole.Lead);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId);

        result.Should().Contain(SystemTeamIds.Volunteers);
        result.Should().Contain(SystemTeamIds.Leads);
    }

    [Fact]
    public async Task GetRequiredTeamIdsForUserAsync_ExcludesLeads_WhenUserIsOnlyMember()
    {
        var userId = Guid.NewGuid();
        var userTeam = SeedTeam("Geeks", SystemTeamType.None);
        SeedTeamMember(userTeam.Id, userId, TeamMemberRole.Member);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId);

        result.Should().Contain(SystemTeamIds.Volunteers);
        result.Should().NotContain(SystemTeamIds.Leads);
    }

    [Fact]
    public async Task GetRequiredTeamIdsForUserAsync_ExcludesLeads_WhenUserIsLeadOfSystemTeam()
    {
        var userId = Guid.NewGuid();
        // Lead of the Volunteers system team should NOT trigger Leads eligibility
        var volunteersTeam = SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        SeedTeamMember(volunteersTeam.Id, userId, TeamMemberRole.Lead);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId);

        result.Should().Contain(SystemTeamIds.Volunteers);
        result.Should().NotContain(SystemTeamIds.Leads);
    }

    [Fact]
    public async Task GetRequiredTeamIdsForUserAsync_ExcludesLeads_WhenLeadMembershipEnded()
    {
        var userId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        var userTeam = SeedTeam("Geeks", SystemTeamType.None);

        _dbContext.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = userTeam.Id,
            UserId = userId,
            Role = TeamMemberRole.Lead,
            JoinedAt = now - Duration.FromDays(30),
            LeftAt = now - Duration.FromDays(1) // Left the team
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId);

        result.Should().Contain(SystemTeamIds.Volunteers);
        result.Should().NotContain(SystemTeamIds.Leads);
    }

    [Fact]
    public async Task GetRequiredTeamIdsForUserAsync_IncludesCurrentTeamMemberships()
    {
        var userId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        var geeks = SeedTeam("Geeks", SystemTeamType.None);
        var volunteers = SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        SeedTeamMember(geeks.Id, userId, TeamMemberRole.Member);
        SeedTeamMember(volunteers.Id, userId, TeamMemberRole.Member);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetRequiredTeamIdsForUserAsync(userId);

        result.Should().Contain(geeks.Id);
        result.Should().Contain(SystemTeamIds.Volunteers);
    }

    // --- GetMembershipSnapshotAsync with Leads docs ---

    [Fact]
    public async Task GetMembershipSnapshotAsync_IncludesLeadsDocsForLeadUser()
    {
        var userId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();

        await SeedProfileAsync(userId, isApproved: true, isSuspended: false);
        await SeedActiveRoleAsync(userId, "Board");

        // System teams
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        var leadsTeam = SeedTeam("Leads", SystemTeamType.Leads, SystemTeamIds.Leads);

        // User-created team where user is Lead
        var geeks = SeedTeam("Geeks", SystemTeamType.None);
        SeedTeamMember(geeks.Id, userId, TeamMemberRole.Lead);

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

        // Leads doc (required)
        var leadsDocId = Guid.NewGuid();
        var leadsVersionId = Guid.NewGuid();
        _dbContext.LegalDocuments.Add(new LegalDocument
        {
            Id = leadsDocId,
            Name = "Lead Agreement",
            TeamId = SystemTeamIds.Leads,
            IsRequired = true,
            IsActive = true,
            GracePeriodDays = 0,
            CurrentCommitSha = "test2",
            CreatedAt = now,
            LastSyncedAt = now
        });
        _dbContext.DocumentVersions.Add(new DocumentVersion
        {
            Id = leadsVersionId,
            LegalDocumentId = leadsDocId,
            VersionNumber = "v1",
            CommitSha = "def456",
            EffectiveFrom = now - Duration.FromDays(1),
            RequiresReConsent = false,
            CreatedAt = now,
            LegalDocument = _dbContext.LegalDocuments.Local.First(d => d.Id == leadsDocId)
        });

        await _dbContext.SaveChangesAsync();

        var snapshot = await _service.GetMembershipSnapshotAsync(userId);

        // Should include both Volunteers and Leads docs
        snapshot.RequiredConsentCount.Should().Be(2);
        snapshot.PendingConsentCount.Should().Be(2);
        snapshot.MissingConsentVersionIds.Should().Contain(volVersionId);
        snapshot.MissingConsentVersionIds.Should().Contain(leadsVersionId);
    }

    [Fact]
    public async Task GetMembershipSnapshotAsync_ExcludesLeadsDocs_WhenUserIsNotLead()
    {
        var userId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();

        await SeedProfileAsync(userId, isApproved: true, isSuspended: false);
        await SeedActiveRoleAsync(userId, "Board");

        // System teams
        SeedTeam("Volunteers", SystemTeamType.Volunteers, SystemTeamIds.Volunteers);
        SeedTeam("Leads", SystemTeamType.Leads, SystemTeamIds.Leads);

        // User is just a member of a user-created team, not a lead
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

        // Leads doc exists but should NOT appear for non-leads
        var leadsDocId = Guid.NewGuid();
        _dbContext.LegalDocuments.Add(new LegalDocument
        {
            Id = leadsDocId,
            Name = "Lead Agreement",
            TeamId = SystemTeamIds.Leads,
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
            LegalDocumentId = leadsDocId,
            VersionNumber = "v1",
            CommitSha = "def456",
            EffectiveFrom = now - Duration.FromDays(1),
            RequiresReConsent = false,
            CreatedAt = now,
            LegalDocument = _dbContext.LegalDocuments.Local.First(d => d.Id == leadsDocId)
        });

        await _dbContext.SaveChangesAsync();

        var snapshot = await _service.GetMembershipSnapshotAsync(userId);

        // Should only include Volunteers doc, not Leads
        snapshot.RequiredConsentCount.Should().Be(1);
        snapshot.PendingConsentCount.Should().Be(1);
        snapshot.MissingConsentVersionIds.Should().ContainSingle().Which.Should().Be(volVersionId);
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
}

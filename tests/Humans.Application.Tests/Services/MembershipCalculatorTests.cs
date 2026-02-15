using FluentAssertions;
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

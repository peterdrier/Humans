using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Xunit;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.Tests.Services;

public class ProfileServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly ProfileService _service;
    private readonly IOnboardingService _onboardingService = Substitute.For<IOnboardingService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IMembershipCalculator _membershipCalculator = Substitute.For<IMembershipCalculator>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    public ProfileServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        _service = new ProfileService(
            _dbContext, _onboardingService, _emailService, _auditLogService,
            _membershipCalculator, _clock, _cache,
            NullLogger<ProfileService>.Instance);

        // Default: return all input IDs as Active (sufficient for most tests that don't filter by status)
        _membershipCalculator
            .PartitionUsersAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ids = callInfo.Arg<IEnumerable<Guid>>().ToHashSet();
                return Task.FromResult(new MembershipPartition(
                    IncompleteSignup: [],
                    PendingApproval: [],
                    Active: ids,
                    MissingConsents: [],
                    Suspended: [],
                    PendingDeletion: []));
            });
    }

    public void Dispose()
    {
        _cache.Dispose();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- Profile save flow ---

    [Fact]
    public async Task SaveProfileAsync_NewProfile_CreatesProfile()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var request = MakeRequest(burnerName: "Flame", firstName: "Jane", lastName: "Doe");

        var profileId = await _service.SaveProfileAsync(userId, "Jane Doe", request, "en");

        profileId.Should().NotBe(Guid.Empty);
        var profile = await _dbContext.Profiles.FirstAsync(p => p.UserId == userId);
        profile.BurnerName.Should().Be("Flame");
        profile.FirstName.Should().Be("Jane");
        profile.LastName.Should().Be("Doe");
    }

    [Fact]
    public async Task SaveProfileAsync_ExistingProfile_UpdatesFields()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        var request = MakeRequest(burnerName: "NewName", firstName: "Updated", lastName: "Person");

        await _service.SaveProfileAsync(userId, "Updated Person", request, "en");

        var profile = await _dbContext.Profiles.FirstAsync(p => p.UserId == userId);
        profile.BurnerName.Should().Be("NewName");
        profile.FirstName.Should().Be("Updated");
        profile.UpdatedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [Fact]
    public async Task SaveProfileAsync_UpdatesUserDisplayName()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        var request = MakeRequest();

        await _service.SaveProfileAsync(userId, "New Display Name", request, "en");

        var user = await _dbContext.Users.FirstAsync(u => u.Id == userId);
        user.DisplayName.Should().Be("New Display Name");
    }

    [Fact]
    public async Task SaveProfileAsync_ParsesBirthday_ValidDate()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        var request = MakeRequest(birthdayMonth: 2, birthdayDay: 14);

        await _service.SaveProfileAsync(userId, "Test", request, "en");

        var profile = await _dbContext.Profiles.FirstAsync(p => p.UserId == userId);
        profile.DateOfBirth.Should().Be(new LocalDate(4, 2, 14));
    }

    [Fact]
    public async Task SaveProfileAsync_ParsesBirthday_InvalidDay_SetsNull()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        var request = MakeRequest(birthdayMonth: 2, birthdayDay: 30);

        await _service.SaveProfileAsync(userId, "Test", request, "en");

        var profile = await _dbContext.Profiles.FirstAsync(p => p.UserId == userId);
        profile.DateOfBirth.Should().BeNull();
    }

    [Fact]
    public async Task SaveProfileAsync_RemoveProfilePicture_ClearsData()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, withPicture: true);
        var request = MakeRequest(removeProfilePicture: true);

        await _service.SaveProfileAsync(userId, "Test", request, "en");

        var profile = await _dbContext.Profiles.FirstAsync(p => p.UserId == userId);
        profile.ProfilePictureData.Should().BeNull();
        profile.ProfilePictureContentType.Should().BeNull();
    }

    [Fact]
    public async Task SaveProfileAsync_CallsSetConsentCheckPending()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        var request = MakeRequest();

        await _service.SaveProfileAsync(userId, "Test", request, "en");

        await _onboardingService.Received().SetConsentCheckPendingIfEligibleAsync(userId, Arg.Any<CancellationToken>());
    }

    // --- Profile save flow: tier application during initial setup ---

    [Fact]
    public async Task SaveProfileAsync_InitialSetup_Colaborador_CreatesApplication()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, isApproved: false);
        var request = MakeRequest(
            selectedTier: MembershipTier.Colaborador,
            applicationMotivation: "I want to help");

        await _service.SaveProfileAsync(userId, "Test", request, "en");

        var app = await _dbContext.Applications.FirstOrDefaultAsync(a => a.UserId == userId);
        app.Should().NotBeNull();
        app!.MembershipTier.Should().Be(MembershipTier.Colaborador);
        app.Motivation.Should().Be("I want to help");
    }

    [Fact]
    public async Task SaveProfileAsync_InitialSetup_Volunteer_NoApplication()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, isApproved: false);
        var request = MakeRequest(selectedTier: MembershipTier.Volunteer);

        await _service.SaveProfileAsync(userId, "Test", request, "en");

        var app = await _dbContext.Applications.FirstOrDefaultAsync(a => a.UserId == userId);
        app.Should().BeNull();
    }

    [Fact]
    public async Task SaveProfileAsync_InitialSetup_ExistingPendingApp_DoesNotDuplicate()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, isApproved: false);
        _dbContext.Applications.Add(new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MembershipTier = MembershipTier.Colaborador,
            Motivation = "Original motivation",
            SubmittedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        // Server-side enforcement: existing pending app forces selectedTier to profile.MembershipTier (Volunteer),
        // so the tier application block is skipped entirely — no duplication, no modification
        var request = MakeRequest(
            selectedTier: MembershipTier.Colaborador,
            applicationMotivation: "New motivation");

        await _service.SaveProfileAsync(userId, "Test", request, "en");

        var apps = await _dbContext.Applications.Where(a => a.UserId == userId).ToListAsync();
        apps.Should().HaveCount(1);
        apps[0].Motivation.Should().Be("Original motivation");
    }

    [Fact]
    public async Task SaveProfileAsync_ApprovedProfile_IgnoresTierSelection()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, isApproved: true);
        var request = MakeRequest(
            selectedTier: MembershipTier.Colaborador,
            applicationMotivation: "Motivation");

        await _service.SaveProfileAsync(userId, "Test", request, "en");

        var app = await _dbContext.Applications.FirstOrDefaultAsync(a => a.UserId == userId);
        app.Should().BeNull();
    }

    // --- Deletion request flow ---

    [Fact]
    public async Task RequestDeletionAsync_ValidUser_SetsDeletionDates()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        var result = await _service.RequestDeletionAsync(userId);

        result.Success.Should().BeTrue();
        var user = await _dbContext.Users.FirstAsync(u => u.Id == userId);
        user.DeletionRequestedAt.Should().Be(_clock.GetCurrentInstant());
        user.DeletionScheduledFor.Should().Be(_clock.GetCurrentInstant().Plus(Duration.FromDays(30)));
    }

    [Fact]
    public async Task RequestDeletionAsync_RevokesTeamMemberships()
    {
        var userId = Guid.NewGuid();
        var user = await SeedUserAsync(userId);
        var teamId = Guid.NewGuid();
        _dbContext.Teams.Add(new Team
        {
            Id = teamId,
            Name = "Test",
            Slug = "test",
            IsActive = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
        _dbContext.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Role = TeamMemberRole.Member,
            JoinedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        await _service.RequestDeletionAsync(userId);

        var membership = await _dbContext.TeamMembers.FirstAsync(tm => tm.UserId == userId);
        membership.LeftAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RequestDeletionAsync_RevokesRoleAssignments()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        _dbContext.RoleAssignments.Add(new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleName = "Board",
            ValidFrom = _clock.GetCurrentInstant() - Duration.FromDays(10),
            CreatedAt = _clock.GetCurrentInstant(),
            CreatedByUserId = Guid.NewGuid()
        });
        await _dbContext.SaveChangesAsync();

        await _service.RequestDeletionAsync(userId);

        var role = await _dbContext.RoleAssignments.FirstAsync(r => r.UserId == userId);
        role.ValidTo.Should().NotBeNull();
    }

    [Fact]
    public async Task RequestDeletionAsync_AlreadyPending_ReturnsError()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var user = await _dbContext.Users.FirstAsync(u => u.Id == userId);
        user.DeletionRequestedAt = _clock.GetCurrentInstant();
        user.DeletionScheduledFor = _clock.GetCurrentInstant().Plus(Duration.FromDays(30));
        await _dbContext.SaveChangesAsync();

        var result = await _service.RequestDeletionAsync(userId);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("AlreadyPending");
    }

    [Fact]
    public async Task RequestDeletionAsync_WritesAuditLog()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        await _service.RequestDeletionAsync(userId);

        await _auditLogService.Received().LogAsync(
            AuditAction.MembershipsRevokedOnDeletionRequest,
            nameof(User), userId, Arg.Any<string>(), userId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task RequestDeletionAsync_SendsDeletionEmail()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        await _service.RequestDeletionAsync(userId);

        await _emailService.Received().SendAccountDeletionRequestedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- Cancel deletion flow ---

    [Fact]
    public async Task CancelDeletionAsync_PendingDeletion_ClearsDates()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var user = await _dbContext.Users.FirstAsync(u => u.Id == userId);
        user.DeletionRequestedAt = _clock.GetCurrentInstant();
        user.DeletionScheduledFor = _clock.GetCurrentInstant().Plus(Duration.FromDays(30));
        await _dbContext.SaveChangesAsync();

        var result = await _service.CancelDeletionAsync(userId);

        result.Success.Should().BeTrue();
        var updated = await _dbContext.Users.FirstAsync(u => u.Id == userId);
        updated.DeletionRequestedAt.Should().BeNull();
        updated.DeletionScheduledFor.Should().BeNull();
    }

    [Fact]
    public async Task CancelDeletionAsync_NoDeletion_ReturnsError()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        var result = await _service.CancelDeletionAsync(userId);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NoDeletionPending");
    }

    // --- Simple lookups ---

    [Fact]
    public async Task GetProfileAsync_ExistingUser_ReturnsProfileWithUser()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        var result = await _service.GetProfileAsync(userId);

        result.Should().NotBeNull();
        result!.UserId.Should().Be(userId);
        result.User.Should().NotBeNull();
        result.User.Id.Should().Be(userId);
    }

    [Fact]
    public async Task GetProfileAsync_NoProfile_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        var result = await _service.GetProfileAsync(userId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetProfilePictureAsync_WithPicture_ReturnsData()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, withPicture: true);
        var profile = await _dbContext.Profiles.FirstAsync(p => p.UserId == userId);

        var (data, contentType) = await _service.GetProfilePictureAsync(profile.Id);

        data.Should().NotBeNull();
        data.Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
        contentType.Should().Be("image/png");
    }

    [Fact]
    public async Task GetProfilePictureAsync_NoPicture_ReturnsNulls()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, withPicture: false);
        var profile = await _dbContext.Profiles.FirstAsync(p => p.UserId == userId);

        var (data, contentType) = await _service.GetProfilePictureAsync(profile.Id);

        data.Should().BeNull();
        contentType.Should().BeNull();
    }

    [Fact]
    public async Task GetProfilePictureAsync_NoProfile_ReturnsNulls()
    {
        var (data, contentType) = await _service.GetProfilePictureAsync(Guid.NewGuid());

        data.Should().BeNull();
        contentType.Should().BeNull();
    }

    [Fact]
    public async Task GetTierCountsAsync_CorrectCounts()
    {
        // 1 Colaborador non-suspended, 1 Colaborador suspended, 1 Asociado non-suspended
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var u3 = Guid.NewGuid();
        await SeedUserAsync(u1);
        await SeedUserAsync(u2);
        await SeedUserAsync(u3);
        await _dbContext.Profiles.AddRangeAsync(
            MakeProfile(u1, MembershipTier.Colaborador, isSuspended: false),
            MakeProfile(u2, MembershipTier.Colaborador, isSuspended: true),
            MakeProfile(u3, MembershipTier.Asociado, isSuspended: false));
        await _dbContext.SaveChangesAsync();

        var (colaboradorCount, asociadoCount) = await _service.GetTierCountsAsync();

        colaboradorCount.Should().Be(1);
        asociadoCount.Should().Be(1);
    }

    // --- Index/edit data ---

    [Fact]
    public async Task GetProfileIndexDataAsync_ReturnsProfileAndLatestApp()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        var olderApp = new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MembershipTier = MembershipTier.Colaborador,
            Motivation = "first",
            SubmittedAt = _clock.GetCurrentInstant() - Duration.FromDays(5),
            UpdatedAt = _clock.GetCurrentInstant() - Duration.FromDays(5)
        };
        var newerApp = new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MembershipTier = MembershipTier.Asociado,
            Motivation = "second",
            SubmittedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        await _dbContext.Applications.AddRangeAsync(olderApp, newerApp);
        await _dbContext.SaveChangesAsync();

        _membershipCalculator.GetMembershipSnapshotAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new MembershipSnapshot(MembershipStatus.Active, true, 3, 2, new List<Guid>()));

        var (profile, latestApp, pendingConsentCount) = await _service.GetProfileIndexDataAsync(userId);

        profile.Should().NotBeNull();
        latestApp.Should().NotBeNull();
        latestApp!.Id.Should().Be(newerApp.Id);
        pendingConsentCount.Should().Be(2);
    }

    [Fact]
    public async Task GetProfileIndexDataAsync_NoProfile_ReturnsNulls()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        _membershipCalculator.GetMembershipSnapshotAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new MembershipSnapshot(MembershipStatus.Pending, false, 0, 0, new List<Guid>()));

        var (profile, latestApp, _) = await _service.GetProfileIndexDataAsync(userId);

        profile.Should().BeNull();
        latestApp.Should().BeNull();
    }

    [Fact]
    public async Task GetProfileEditDataAsync_SubmittedApp_IsTierLocked()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, isApproved: false);
        _dbContext.Applications.Add(new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MembershipTier = MembershipTier.Colaborador,
            Motivation = "test",
            SubmittedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        var (profile, isTierLocked, pendingApp) = await _service.GetProfileEditDataAsync(userId);

        profile.Should().NotBeNull();
        isTierLocked.Should().BeTrue();
        pendingApp.Should().NotBeNull();
    }

    [Fact]
    public async Task GetProfileEditDataAsync_ApprovedApp_IsTierLocked()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, isApproved: true);
        var app = new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MembershipTier = MembershipTier.Colaborador,
            Motivation = "test",
            SubmittedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        app.Approve(userId, null, _clock);
        _dbContext.Applications.Add(app);
        await _dbContext.SaveChangesAsync();

        var (_, isTierLocked, pendingApp) = await _service.GetProfileEditDataAsync(userId);

        isTierLocked.Should().BeTrue();
        // Profile is approved, so PendingApplication is null even though app exists
        pendingApp.Should().BeNull();
    }

    [Fact]
    public async Task GetProfileEditDataAsync_NoApps_NotLocked()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        var (profile, isTierLocked, pendingApp) = await _service.GetProfileEditDataAsync(userId);

        profile.Should().NotBeNull();
        isTierLocked.Should().BeFalse();
        pendingApp.Should().BeNull();
    }

    [Fact]
    public async Task GetProfileEditDataAsync_NoProfile_ReturnsNulls()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        var (profile, isTierLocked, pendingApp) = await _service.GetProfileEditDataAsync(userId);

        profile.Should().BeNull();
        isTierLocked.Should().BeFalse();
        pendingApp.Should().BeNull();
    }

    // --- Batch/filtered queries ---

    [Fact]
    public async Task GetCustomPictureInfoByUserIdsAsync_WithPictures_ReturnsTuples()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var u3 = Guid.NewGuid();
        await SeedUserWithProfileAsync(u1, isApproved: true, withPicture: true);
        await SeedUserWithProfileAsync(u2, isApproved: true, withPicture: true);
        await SeedUserWithProfileAsync(u3, isApproved: true, withPicture: false);

        var result = await _service.GetCustomPictureInfoByUserIdsAsync(new[] { u1, u2, u3 });

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetCustomPictureInfoByUserIdsAsync_EmptyInput_ReturnsEmpty()
    {
        var result = await _service.GetCustomPictureInfoByUserIdsAsync(Array.Empty<Guid>());

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBirthdayProfilesAsync_MatchesMonth_OrderedByDay()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var u3 = Guid.NewGuid();
        await SeedUserAsync(u1);
        await SeedUserAsync(u2);
        await SeedUserAsync(u3);

        var p1 = MakeProfile(u1, isApproved: true);
        p1.DateOfBirth = new LocalDate(4, 3, 20);
        var p2 = MakeProfile(u2, isApproved: true);
        p2.DateOfBirth = new LocalDate(4, 3, 5);
        var p3 = MakeProfile(u3, isApproved: true);
        p3.DateOfBirth = new LocalDate(4, 6, 15);
        await _dbContext.Profiles.AddRangeAsync(p1, p2, p3);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetBirthdayProfilesAsync(3);

        result.Should().HaveCount(2);
        result[0].Day.Should().Be(5);
        result[1].Day.Should().Be(20);
    }

    [Fact]
    public async Task GetBirthdayProfilesAsync_ExcludesSuspended()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        await SeedUserAsync(u1);
        await SeedUserAsync(u2);

        var p1 = MakeProfile(u1, isApproved: true, isSuspended: true);
        p1.DateOfBirth = new LocalDate(4, 3, 10);
        var p2 = MakeProfile(u2, isApproved: true);
        p2.DateOfBirth = new LocalDate(4, 3, 15);
        await _dbContext.Profiles.AddRangeAsync(p1, p2);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetBirthdayProfilesAsync(3);

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(u2);
    }

    [Fact]
    public async Task GetBirthdayProfilesAsync_NoMatches_ReturnsEmpty()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var profile = MakeProfile(userId);
        profile.DateOfBirth = new LocalDate(4, 6, 10);
        _dbContext.Profiles.Add(profile);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetBirthdayProfilesAsync(3);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetApprovedProfilesWithLocationAsync_ReturnsApprovedWithCoordinates()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var profile = MakeProfile(userId, isApproved: true);
        profile.Latitude = 40.0;
        profile.Longitude = -3.0;
        _dbContext.Profiles.Add(profile);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetApprovedProfilesWithLocationAsync();

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(userId);
        result[0].Latitude.Should().Be(40.0);
        result[0].Longitude.Should().Be(-3.0);
    }

    [Fact]
    public async Task GetApprovedProfilesWithLocationAsync_ExcludesSuspendedAndUnapproved()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var u3 = Guid.NewGuid();
        await SeedUserAsync(u1);
        await SeedUserAsync(u2);
        await SeedUserAsync(u3);

        // Suspended with location
        var p1 = MakeProfile(u1, isApproved: true, isSuspended: true);
        p1.Latitude = 40.0;
        p1.Longitude = -3.0;
        // Unapproved with location
        var p2 = MakeProfile(u2, isApproved: false);
        p2.Latitude = 41.0;
        p2.Longitude = -2.0;
        // Approved without location
        var p3 = MakeProfile(u3, isApproved: true);
        await _dbContext.Profiles.AddRangeAsync(p1, p2, p3);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetApprovedProfilesWithLocationAsync();

        result.Should().BeEmpty();
    }

    // --- Admin queries ---

    [Fact]
    public async Task GetFilteredHumansAsync_NoFilter_ReturnsAll()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        await SeedUserAsync(u1);
        await SeedUserAsync(u2);

        var result = await _service.GetFilteredHumansAsync(null, null);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetFilteredHumansAsync_SearchByEmail()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        await SeedUserAsync(u1);
        await SeedUserAsync(u2);
        var user2 = await _dbContext.Users.FirstAsync(u => u.Id == u2);
        user2.Email = "special@example.com";
        user2.UserName = "special@example.com";
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetFilteredHumansAsync("special", null);

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(u2);
    }

    [Fact]
    public async Task GetFilteredHumansAsync_SearchByDisplayName()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        await SeedUserAsync(u1);
        await SeedUserAsync(u2);
        var user2 = await _dbContext.Users.FirstAsync(u => u.Id == u2);
        user2.DisplayName = "UniqueFlame";
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetFilteredHumansAsync("UniqueFlame", null);

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(u2);
    }

    [Fact]
    public async Task GetFilteredHumansAsync_StatusActive()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        await SeedUserAsync(u1);
        await SeedUserAsync(u2);
        _dbContext.Profiles.Add(MakeProfile(u1, isApproved: true));
        _dbContext.Profiles.Add(MakeProfile(u2, isApproved: false));
        await _dbContext.SaveChangesAsync();

        // u1 is Active, u2 is PendingApproval — partition must reflect this for filter to work
        _membershipCalculator
            .PartitionUsersAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ids = callInfo.Arg<IEnumerable<Guid>>().ToHashSet();
                return Task.FromResult(new MembershipPartition(
                    IncompleteSignup: [],
                    PendingApproval: ids.Where(id => id == u2).ToHashSet(),
                    Active: ids.Where(id => id == u1).ToHashSet(),
                    MissingConsents: [],
                    Suspended: [],
                    PendingDeletion: []));
            });

        var result = await _service.GetFilteredHumansAsync(null, "active");

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(u1);
    }

    [Fact]
    public async Task GetFilteredHumansAsync_StatusPending()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        await SeedUserAsync(u1);
        await SeedUserAsync(u2);
        _dbContext.Profiles.Add(MakeProfile(u1, isApproved: true));
        _dbContext.Profiles.Add(MakeProfile(u2, isApproved: false));
        await _dbContext.SaveChangesAsync();

        _membershipCalculator
            .PartitionUsersAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ids = ci.Arg<IEnumerable<Guid>>().ToHashSet();
                return Task.FromResult(new MembershipPartition(
                    IncompleteSignup: [],
                    PendingApproval: ids.Where(id => id == u2).ToHashSet(),
                    Active: ids.Where(id => id == u1).ToHashSet(),
                    MissingConsents: [],
                    Suspended: [],
                    PendingDeletion: []));
            });

        var result = await _service.GetFilteredHumansAsync(null, "pending");

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(u2);
    }

    [Fact]
    public async Task GetFilteredHumansAsync_StatusSuspended()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        await SeedUserAsync(u1);
        await SeedUserAsync(u2);
        _dbContext.Profiles.Add(MakeProfile(u1, isSuspended: true));
        _dbContext.Profiles.Add(MakeProfile(u2));
        await _dbContext.SaveChangesAsync();

        _membershipCalculator
            .PartitionUsersAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ids = ci.Arg<IEnumerable<Guid>>().ToHashSet();
                return Task.FromResult(new MembershipPartition(
                    IncompleteSignup: [],
                    PendingApproval: [],
                    Active: ids.Where(id => id == u2).ToHashSet(),
                    MissingConsents: [],
                    Suspended: ids.Where(id => id == u1).ToHashSet(),
                    PendingDeletion: []));
            });

        var result = await _service.GetFilteredHumansAsync(null, "suspended");

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(u1);
    }

    [Fact]
    public async Task GetAdminHumanDetailAsync_ReturnsFullDetail()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, isApproved: true);

        _dbContext.Applications.Add(new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MembershipTier = MembershipTier.Colaborador,
            Motivation = "test",
            SubmittedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
        _dbContext.RoleAssignments.Add(new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleName = "Board",
            ValidFrom = _clock.GetCurrentInstant() - Duration.FromDays(10),
            CreatedAt = _clock.GetCurrentInstant(),
            CreatedByUserId = userId
        });
        _dbContext.AuditLogEntries.Add(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Action = AuditAction.MembershipsRevokedOnDeletionRequest,
            EntityType = "User",
            EntityId = userId,
            Description = "Test entry",
            OccurredAt = _clock.GetCurrentInstant(),
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetAdminHumanDetailAsync(userId);

        result.Should().NotBeNull();
        result!.User.Id.Should().Be(userId);
        result.Profile.Should().NotBeNull();
        result.Applications.Should().HaveCount(1);
        result.RoleAssignments.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetAdminHumanDetailAsync_NonExistent_ReturnsNull()
    {
        var result = await _service.GetAdminHumanDetailAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    // --- Cooldown and export ---

    [Fact]
    public async Task GetEmailCooldownInfoAsync_WithinCooldown_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var emailId = Guid.NewGuid();
        _dbContext.UserEmails.Add(new UserEmail
        {
            Id = emailId,
            UserId = userId,
            Email = "test@test.com",
            VerificationSentAt = _clock.GetCurrentInstant() - Duration.FromMinutes(2),
            DisplayOrder = 0
        });
        await _dbContext.SaveChangesAsync();

        var (canAdd, minutesUntilResend, pendingEmailId) = await _service.GetEmailCooldownInfoAsync(emailId);

        canAdd.Should().BeFalse();
        minutesUntilResend.Should().BeGreaterThan(0);
        pendingEmailId.Should().Be(emailId);
    }

    [Fact]
    public async Task GetEmailCooldownInfoAsync_AfterCooldown_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var emailId = Guid.NewGuid();
        _dbContext.UserEmails.Add(new UserEmail
        {
            Id = emailId,
            UserId = userId,
            Email = "test@test.com",
            VerificationSentAt = _clock.GetCurrentInstant() - Duration.FromMinutes(6),
            DisplayOrder = 0
        });
        await _dbContext.SaveChangesAsync();

        var (canAdd, minutesUntilResend, pendingEmailId) = await _service.GetEmailCooldownInfoAsync(emailId);

        canAdd.Should().BeTrue();
        minutesUntilResend.Should().Be(0);
        pendingEmailId.Should().BeNull();
    }

    [Fact]
    public async Task GetEmailCooldownInfoAsync_NoVerificationSent_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var emailId = Guid.NewGuid();
        _dbContext.UserEmails.Add(new UserEmail
        {
            Id = emailId,
            UserId = userId,
            Email = "test@test.com",
            VerificationSentAt = null,
            DisplayOrder = 0
        });
        await _dbContext.SaveChangesAsync();

        var (canAdd, minutesUntilResend, pendingEmailId) = await _service.GetEmailCooldownInfoAsync(emailId);

        canAdd.Should().BeTrue();
        minutesUntilResend.Should().Be(0);
        pendingEmailId.Should().BeNull();
    }

    [Fact]
    public async Task ExportDataAsync_ReturnsNonNull()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        var result = await _service.ExportDataAsync(userId);

        result.Should().NotBeNull();
    }

    // --- Search ---

    [Fact]
    public async Task SearchHumansAsync_MatchesByDisplayName()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var profile = MakeProfile(userId, isApproved: true);
        _dbContext.Profiles.Add(profile);
        await _dbContext.SaveChangesAsync();
        var user = await _dbContext.Users.FirstAsync(u => u.Id == userId);
        user.DisplayName = "Sparkle Phoenix";
        await _dbContext.SaveChangesAsync();

        var results = await _service.SearchHumansAsync("Sparkle");

        results.Should().HaveCount(1);
        results[0].UserId.Should().Be(userId);
        results[0].MatchField.Should().Be("Name");
    }

    [Fact]
    public async Task SearchHumansAsync_MatchesByCity()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var profile = MakeProfile(userId, isApproved: true);
        profile.City = "Barcelona";
        _dbContext.Profiles.Add(profile);
        await _dbContext.SaveChangesAsync();

        var results = await _service.SearchHumansAsync("Barcelona");

        results.Should().HaveCount(1);
        results[0].MatchField.Should().Be("City");
    }

    [Fact]
    public async Task SearchHumansAsync_MatchesByBio()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var profile = MakeProfile(userId, isApproved: true);
        profile.Bio = "I love fire dancing and community building";
        _dbContext.Profiles.Add(profile);
        await _dbContext.SaveChangesAsync();

        var results = await _service.SearchHumansAsync("fire dancing");

        results.Should().HaveCount(1);
        results[0].MatchField.Should().Be("Bio");
        results[0].MatchSnippet.Should().Contain("fire dancing");
    }

    [Fact]
    public async Task SearchHumansAsync_MatchesByInterests()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var profile = MakeProfile(userId, isApproved: true);
        profile.ContributionInterests = "Sound engineering and DJing";
        _dbContext.Profiles.Add(profile);
        await _dbContext.SaveChangesAsync();

        var results = await _service.SearchHumansAsync("sound");

        results.Should().HaveCount(1);
        results[0].MatchField.Should().Be("Interests");
    }

    [Fact]
    public async Task SearchHumansAsync_MatchesByBurnerName()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var profile = MakeProfile(userId, isApproved: true);
        profile.BurnerName = "Phoenix Rising";
        _dbContext.Profiles.Add(profile);
        await _dbContext.SaveChangesAsync();

        var results = await _service.SearchHumansAsync("Phoenix");

        results.Should().HaveCount(1);
        results[0].MatchField.Should().Be("Burner Name");
    }

    [Fact]
    public async Task SearchHumansAsync_MatchesByVolunteerHistory()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var profile = MakeProfile(userId, isApproved: true);
        _dbContext.Profiles.Add(profile);
        await _dbContext.SaveChangesAsync();

        _dbContext.VolunteerHistoryEntries.Add(new VolunteerHistoryEntry
        {
            Id = Guid.NewGuid(),
            ProfileId = profile.Id,
            Date = new NodaTime.LocalDate(2025, 6, 1),
            EventName = "Burning Man 2025",
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        var results = await _service.SearchHumansAsync("Burning Man");

        results.Should().HaveCount(1);
        results[0].MatchField.Should().Be("Burner CV");
    }

    [Fact]
    public async Task SearchHumansAsync_ExcludesSuspended()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        await SeedUserAsync(u1);
        await SeedUserAsync(u2);
        var p1 = MakeProfile(u1, isApproved: true, isSuspended: true);
        p1.City = "Madrid";
        var p2 = MakeProfile(u2, isApproved: true);
        p2.City = "Madrid";
        await _dbContext.Profiles.AddRangeAsync(p1, p2);
        await _dbContext.SaveChangesAsync();

        var results = await _service.SearchHumansAsync("Madrid");

        results.Should().HaveCount(1);
        results[0].UserId.Should().Be(u2);
    }

    [Fact]
    public async Task SearchHumansAsync_CaseInsensitive()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var profile = MakeProfile(userId, isApproved: true);
        profile.City = "Berlin";
        _dbContext.Profiles.Add(profile);
        await _dbContext.SaveChangesAsync();

        var results = await _service.SearchHumansAsync("berlin");

        results.Should().HaveCount(1);
    }

    [Fact]
    public async Task SearchHumansAsync_ResultsOrderedByName()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var u3 = Guid.NewGuid();
        await SeedUserAsync(u1);
        await SeedUserAsync(u2);
        await SeedUserAsync(u3);
        var user1 = await _dbContext.Users.FirstAsync(u => u.Id == u1);
        user1.DisplayName = "Zara";
        var user2 = await _dbContext.Users.FirstAsync(u => u.Id == u2);
        user2.DisplayName = "Alice";
        var user3 = await _dbContext.Users.FirstAsync(u => u.Id == u3);
        user3.DisplayName = "Maria";
        var p1 = MakeProfile(u1, isApproved: true);
        p1.City = "TestCity";
        var p2 = MakeProfile(u2, isApproved: true);
        p2.City = "TestCity";
        var p3 = MakeProfile(u3, isApproved: true);
        p3.City = "TestCity";
        await _dbContext.Profiles.AddRangeAsync(p1, p2, p3);
        await _dbContext.SaveChangesAsync();

        var results = await _service.SearchHumansAsync("TestCity");

        results.Should().HaveCount(3);
        results[0].DisplayName.Should().Be("Alice");
        results[1].DisplayName.Should().Be("Maria");
        results[2].DisplayName.Should().Be("Zara");
    }

    [Fact]
    public async Task SearchHumansAsync_NoMatch_ReturnsEmpty()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var profile = MakeProfile(userId, isApproved: true);
        _dbContext.Profiles.Add(profile);
        await _dbContext.SaveChangesAsync();

        var results = await _service.SearchHumansAsync("zzzznonexistent");

        results.Should().BeEmpty();
    }

    // --- Cache behavior ---

    [Fact]
    public async Task UpdateProfileCache_AddAndRemove()
    {
        var userId = Guid.NewGuid();
        var cached = new CachedProfile(
            userId, "Test", null, false, Guid.NewGuid(), 123,
            null, null, null, null, null, null, null, null, null, null, []);

        // Cache not loaded yet — should not throw
        _service.UpdateProfileCache(userId, cached);

        // Load cache first
        await SeedUserAsync(userId);
        var profile = MakeProfile(userId, isApproved: true);
        _dbContext.Profiles.Add(profile);
        await _dbContext.SaveChangesAsync();
        await _service.GetApprovedProfilesWithLocationAsync(); // triggers cache load

        // Now add
        _service.UpdateProfileCache(userId, cached);
        var searchResults = await _service.SearchHumansAsync("Test");
        searchResults.Should().Contain(r => r.UserId == userId);

        // Remove
        _service.UpdateProfileCache(userId, null);
        searchResults = await _service.SearchHumansAsync("Test");
        searchResults.Should().NotContain(r => r.UserId == userId);
    }

    [Fact]
    public async Task SaveProfileAsync_UpdatesCacheForApprovedProfile()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, isApproved: true);

        // Prime cache
        await _service.GetApprovedProfilesWithLocationAsync();

        // Save with new city
        var request = MakeRequest(city: "Valencia");
        await _service.SaveProfileAsync(userId, "Test User", request, "en");

        // Search should find the updated profile
        var results = await _service.SearchHumansAsync("Valencia");
        results.Should().HaveCount(1);
        results[0].UserId.Should().Be(userId);
    }

    [Fact]
    public async Task RequestDeletionAsync_RemovesFromCache()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, isApproved: true);

        // Prime cache
        await _service.GetApprovedProfilesWithLocationAsync();
        var resultsBefore = await _service.SearchHumansAsync("Test User");
        resultsBefore.Should().Contain(r => r.UserId == userId);

        // Request deletion
        await _service.RequestDeletionAsync(userId);

        // Should be gone from cache
        var resultsAfter = await _service.SearchHumansAsync("Test User");
        resultsAfter.Should().NotContain(r => r.UserId == userId);
    }

    [Fact]
    public async Task RequestDeletionAsync_InvalidatesRoleAndShiftAuthorizationCaches()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        _cache.Set(CacheKeys.ActiveTeams, new object());
        _cache.Set(CacheKeys.RoleAssignmentClaims(userId), new[] { "stale-claim" });
        _cache.Set(CacheKeys.ShiftAuthorization(userId), new[] { Guid.NewGuid() });

        await _service.RequestDeletionAsync(userId);

        _cache.TryGetValue(CacheKeys.ActiveTeams, out _).Should().BeFalse();
        _cache.TryGetValue(CacheKeys.RoleAssignmentClaims(userId), out _).Should().BeFalse();
        _cache.TryGetValue(CacheKeys.ShiftAuthorization(userId), out _).Should().BeFalse();
    }

    // --- Helpers ---

    private async Task<User> SeedUserAsync(Guid userId)
    {
        var user = new User
        {
            Id = userId,
            DisplayName = "Test User",
            UserName = $"test-{userId}@test.com",
            Email = $"test-{userId}@test.com",
            PreferredLanguage = "en"
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();
        return user;
    }

    private async Task SeedUserWithProfileAsync(Guid userId,
        bool isApproved = false, bool withPicture = false)
    {
        await SeedUserAsync(userId);
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = "OldName",
            FirstName = "Old",
            LastName = "User",
            IsApproved = isApproved,
            CreatedAt = _clock.GetCurrentInstant() - Duration.FromDays(1),
            UpdatedAt = _clock.GetCurrentInstant() - Duration.FromDays(1)
        };
        if (withPicture)
        {
            profile.ProfilePictureData = new byte[] { 1, 2, 3 };
            profile.ProfilePictureContentType = "image/png";
        }
        _dbContext.Profiles.Add(profile);
        await _dbContext.SaveChangesAsync();
    }

    private static ProfileSaveRequest MakeRequest(
        string burnerName = "TestBurner", string firstName = "Test", string lastName = "User",
        int? birthdayMonth = null, int? birthdayDay = null,
        bool removeProfilePicture = false,
        MembershipTier? selectedTier = null, string? applicationMotivation = null,
        string? city = null)
    {
        return new ProfileSaveRequest(
            BurnerName: burnerName, FirstName: firstName, LastName: lastName,
            City: city, CountryCode: null, Latitude: null, Longitude: null, PlaceId: null,
            Bio: null, Pronouns: null, ContributionInterests: null, BoardNotes: null,
            BirthdayMonth: birthdayMonth, BirthdayDay: birthdayDay,
            EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelationship: null,
            NoPriorBurnExperience: false,
            ProfilePictureData: null, ProfilePictureContentType: null,
            RemoveProfilePicture: removeProfilePicture,
            SelectedTier: selectedTier, ApplicationMotivation: applicationMotivation,
            ApplicationAdditionalInfo: null,
            ApplicationSignificantContribution: null, ApplicationRoleUnderstanding: null);
    }

    private Profile MakeProfile(Guid userId, MembershipTier tier = MembershipTier.Volunteer,
        bool isApproved = false, bool isSuspended = false)
    {
        return new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = "Test",
            FirstName = "First",
            LastName = "Last",
            MembershipTier = tier,
            IsApproved = isApproved,
            IsSuspended = isSuspended,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
    }
}

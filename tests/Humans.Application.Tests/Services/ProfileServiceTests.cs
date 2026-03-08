using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
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
            Id = teamId, Name = "Test", Slug = "test", IsActive = true,
            CreatedAt = _clock.GetCurrentInstant(), UpdatedAt = _clock.GetCurrentInstant()
        });
        _dbContext.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(), TeamId = teamId, UserId = userId,
            Role = TeamMemberRole.Member, JoinedAt = _clock.GetCurrentInstant()
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
            Id = Guid.NewGuid(), UserId = userId, RoleName = "Board",
            ValidFrom = _clock.GetCurrentInstant() - Duration.FromDays(10),
            CreatedAt = _clock.GetCurrentInstant(), CreatedByUserId = Guid.NewGuid()
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
            "User", userId, Arg.Any<string>(), userId, Arg.Any<string>(),
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
        MembershipTier? selectedTier = null, string? applicationMotivation = null)
    {
        return new ProfileSaveRequest(
            BurnerName: burnerName, FirstName: firstName, LastName: lastName,
            City: null, CountryCode: null, Latitude: null, Longitude: null, PlaceId: null,
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
}

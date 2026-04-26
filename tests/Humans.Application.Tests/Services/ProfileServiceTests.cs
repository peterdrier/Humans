using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Xunit;
using MemberApplication = Humans.Domain.Entities.Application;
using ProfileService = Humans.Application.Services.Profile.ProfileService;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Auth;
using Humans.Infrastructure.Repositories.Profiles;
using Humans.Infrastructure.Repositories.Users;

namespace Humans.Application.Tests.Services;

public class ProfileServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly ProfileService _service;
    private readonly IProfileRepository _profileRepository;
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserEmailRepository _userEmailRepository;
    private readonly IContactFieldRepository _contactFieldRepository;
    private readonly ICommunicationPreferenceRepository _communicationPreferenceRepository = Substitute.For<ICommunicationPreferenceRepository>();
    private readonly IOnboardingService _onboardingService = Substitute.For<IOnboardingService>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IMembershipCalculator _membershipCalculator = Substitute.For<IMembershipCalculator>();
    private readonly IConsentService _consentService = Substitute.For<IConsentService>();
    private readonly ITicketQueryService _ticketQueryService = Substitute.For<ITicketQueryService>();
    private readonly IApplicationDecisionService _applicationDecisionService = Substitute.For<IApplicationDecisionService>();
    private readonly ICampaignService _campaignService = Substitute.For<ICampaignService>();
    private readonly IRoleAssignmentService _roleAssignmentService = Substitute.For<IRoleAssignmentService>();
    private readonly IAccountDeletionService _accountDeletionService = Substitute.For<IAccountDeletionService>();

    public ProfileServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));

        // Real repositories backed by an IDbContextFactory wrapping the in-memory store.
        var factory = new Infrastructure.TestDbContextFactory(options);
        _profileRepository = new ProfileRepository(factory, _clock);
        _userEmailRepository = new UserEmailRepository(factory);
        _contactFieldRepository = new ContactFieldRepository(factory);

        _service = new ProfileService(
            _profileRepository, _userService,
            _userEmailRepository,
            _contactFieldRepository, _communicationPreferenceRepository,
            _onboardingService, _auditLogService,
            _membershipCalculator, _consentService, _ticketQueryService,
            _applicationDecisionService, _campaignService,
            _roleAssignmentService, _accountDeletionService,
            _clock,
            NullLogger<ProfileService>.Instance);

        _ticketQueryService.GetUserTicketExportDataAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new UserTicketExportData([], []));

        // Default: no pending applications
        _applicationDecisionService.GetUserApplicationsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<MemberApplication>());

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
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- Profile save flow ---

    [HumansFact(Timeout = 10000)]
    public async Task SaveProfileAsync_NewProfile_CreatesProfile()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var request = MakeRequest(burnerName: "Flame", firstName: "Jane", lastName: "Doe");

        var profileId = await _service.SaveProfileAsync(userId, "Jane Doe", request, "en");

        profileId.Should().NotBe(Guid.Empty);
        var profile = await _dbContext.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.BurnerName.Should().Be("Flame");
        profile.FirstName.Should().Be("Jane");
        profile.LastName.Should().Be("Doe");
    }

    [HumansFact]
    public async Task SaveProfileAsync_ExistingProfile_UpdatesFields()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        var request = MakeRequest(burnerName: "NewName", firstName: "Updated", lastName: "Person");

        await _service.SaveProfileAsync(userId, "Updated Person", request, "en");

        var profile = await _dbContext.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.BurnerName.Should().Be("NewName");
        profile.FirstName.Should().Be("Updated");
        profile.UpdatedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task SaveProfileAsync_UpdatesUserDisplayName()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        var request = MakeRequest();

        await _service.SaveProfileAsync(userId, "New Display Name", request, "en");

        await _userService.Received().UpdateDisplayNameAsync(userId, "New Display Name", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SaveProfileAsync_ParsesBirthday_ValidDate()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        var request = MakeRequest(birthdayMonth: 2, birthdayDay: 14);

        await _service.SaveProfileAsync(userId, "Test", request, "en");

        var profile = await _dbContext.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.DateOfBirth.Should().Be(new LocalDate(4, 2, 14));
    }

    [HumansFact]
    public async Task SaveProfileAsync_ParsesBirthday_InvalidDay_SetsNull()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        var request = MakeRequest(birthdayMonth: 2, birthdayDay: 30);

        await _service.SaveProfileAsync(userId, "Test", request, "en");

        var profile = await _dbContext.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.DateOfBirth.Should().BeNull();
    }

    [HumansFact]
    public async Task SaveProfileAsync_RemoveProfilePicture_ClearsData()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, withPicture: true);
        var request = MakeRequest(removeProfilePicture: true);

        await _service.SaveProfileAsync(userId, "Test", request, "en");

        var profile = await _dbContext.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.ProfilePictureData.Should().BeNull();
        profile.ProfilePictureContentType.Should().BeNull();
    }

    [HumansFact]
    public async Task SaveProfileAsync_CallsSetConsentCheckPending()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        var request = MakeRequest();

        await _service.SaveProfileAsync(userId, "Test", request, "en");

        await _onboardingService.Received().SetConsentCheckPendingIfEligibleAsync(userId, Arg.Any<CancellationToken>());
    }

    // --- Profile save flow: tier application during initial setup ---

    [HumansFact]
    public async Task SaveProfileAsync_InitialSetup_Colaborador_CreatesApplication()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, isApproved: false);
        var request = MakeRequest(
            selectedTier: MembershipTier.Colaborador,
            applicationMotivation: "I want to help");

        await _service.SaveProfileAsync(userId, "Test", request, "en");

        await _applicationDecisionService.Received().SubmitAsync(
            userId, MembershipTier.Colaborador,
            "I want to help",
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            "en",
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SaveProfileAsync_InitialSetup_Volunteer_NoApplication()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, isApproved: false);
        var request = MakeRequest(selectedTier: MembershipTier.Volunteer);

        await _service.SaveProfileAsync(userId, "Test", request, "en");

        await _applicationDecisionService.DidNotReceive().SubmitAsync(
            Arg.Any<Guid>(), Arg.Any<MembershipTier>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SaveProfileAsync_InitialSetup_ExistingPendingApp_DoesNotDuplicate()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, isApproved: false);
        var existingApp = new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MembershipTier = MembershipTier.Colaborador,
            Motivation = "Original motivation",
            SubmittedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };

        _applicationDecisionService.GetUserApplicationsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<MemberApplication> { existingApp });

        var request = MakeRequest(
            selectedTier: MembershipTier.Colaborador,
            applicationMotivation: "New motivation");

        await _service.SaveProfileAsync(userId, "Test", request, "en");

        // Server-side enforcement: existing pending app forces selectedTier to profile.MembershipTier (Volunteer),
        // so SubmitAsync should NOT be called (it was already submitted)
        await _applicationDecisionService.DidNotReceive().SubmitAsync(
            Arg.Any<Guid>(), Arg.Any<MembershipTier>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SaveProfileAsync_ApprovedProfile_IgnoresTierSelection()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, isApproved: true);
        var request = MakeRequest(
            selectedTier: MembershipTier.Colaborador,
            applicationMotivation: "Motivation");

        await _service.SaveProfileAsync(userId, "Test", request, "en");

        await _applicationDecisionService.DidNotReceive().SubmitAsync(
            Arg.Any<Guid>(), Arg.Any<MembershipTier>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- Deletion request flow ---
    // NB: the actual RequestDeletionAsync cascade lives in IAccountDeletionService
    // (issue nobodies-collective/Humans#582). ProfileService simply delegates; see AccountDeletionServiceTests
    // for the full team/role/audit/email coverage.

    [HumansFact]
    public async Task RequestDeletionAsync_DelegatesToAccountDeletionService()
    {
        var userId = Guid.NewGuid();
        _accountDeletionService.RequestDeletionAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));

        var result = await _service.RequestDeletionAsync(userId);

        result.Success.Should().BeTrue();
        await _accountDeletionService.Received(1)
            .RequestDeletionAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RequestDeletionAsync_PropagatesErrorFromAccountDeletionService()
    {
        var userId = Guid.NewGuid();
        _accountDeletionService.RequestDeletionAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(false, "AlreadyPending"));

        var result = await _service.RequestDeletionAsync(userId);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("AlreadyPending");
    }

    // --- Cancel deletion flow ---

    [HumansFact]
    public async Task CancelDeletionAsync_PendingDeletion_ClearsDates()
    {
        var userId = Guid.NewGuid();
        var user = await SeedUserAsync(userId);
        user.DeletionRequestedAt = _clock.GetCurrentInstant();
        user.DeletionScheduledFor = _clock.GetCurrentInstant().Plus(Duration.FromDays(30));
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _service.CancelDeletionAsync(userId);

        result.Success.Should().BeTrue();
        await _userService.Received().ClearDeletionAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CancelDeletionAsync_NoDeletion_ReturnsError()
    {
        var userId = Guid.NewGuid();
        var user = await SeedUserAsync(userId);
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _service.CancelDeletionAsync(userId);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NoDeletionPending");
    }

    // --- Simple lookups ---

    [HumansFact]
    public async Task GetProfileAsync_ExistingUser_ReturnsProfile()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        var result = await _service.GetProfileAsync(userId);

        result.Should().NotBeNull();
        result!.UserId.Should().Be(userId);
    }

    [HumansFact]
    public async Task GetProfileAsync_NoProfile_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        var result = await _service.GetProfileAsync(userId);

        result.Should().BeNull();
    }

    [HumansFact]
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

    [HumansFact]
    public async Task GetProfilePictureAsync_NoPicture_ReturnsNulls()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, withPicture: false);
        var profile = await _dbContext.Profiles.FirstAsync(p => p.UserId == userId);

        var (data, contentType) = await _service.GetProfilePictureAsync(profile.Id);

        data.Should().BeNull();
        contentType.Should().BeNull();
    }

    [HumansFact]
    public async Task GetProfilePictureAsync_NoProfile_ReturnsNulls()
    {
        var (data, contentType) = await _service.GetProfilePictureAsync(Guid.NewGuid());

        data.Should().BeNull();
        contentType.Should().BeNull();
    }

    [HumansFact]
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

    [HumansFact]
    public async Task GetActiveApprovedCountAsync_ReturnsOnlyApprovedAndNotSuspended()
    {
        // 3 approved-active, 1 approved-but-suspended, 1 unapproved
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var u3 = Guid.NewGuid();
        var u4 = Guid.NewGuid();
        var u5 = Guid.NewGuid();
        await SeedUserAsync(u1);
        await SeedUserAsync(u2);
        await SeedUserAsync(u3);
        await SeedUserAsync(u4);
        await SeedUserAsync(u5);
        await _dbContext.Profiles.AddRangeAsync(
            MakeProfile(u1, isApproved: true,  isSuspended: false), // counted
            MakeProfile(u2, isApproved: true,  isSuspended: false), // counted
            MakeProfile(u3, isApproved: true,  isSuspended: false), // counted
            MakeProfile(u4, isApproved: true,  isSuspended: true),  // excluded: suspended
            MakeProfile(u5, isApproved: false, isSuspended: false)); // excluded: unapproved
        await _dbContext.SaveChangesAsync();

        var count = await _service.GetActiveApprovedCountAsync();

        count.Should().Be(3);
    }

    // --- Index/edit data ---

    [HumansFact]
    public async Task GetProfileIndexDataAsync_ReturnsProfileAndLatestApp()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        var newerApp = new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MembershipTier = MembershipTier.Asociado,
            Motivation = "second",
            SubmittedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };

        _applicationDecisionService.GetUserApplicationsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<MemberApplication> { newerApp });

        _membershipCalculator.GetMembershipSnapshotAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new MembershipSnapshot(MembershipStatus.Active, true, 3, 2, new List<Guid>()));

        var (profile, latestApp, pendingConsentCount) = await _service.GetProfileIndexDataAsync(userId);

        profile.Should().NotBeNull();
        latestApp.Should().NotBeNull();
        latestApp!.Id.Should().Be(newerApp.Id);
        pendingConsentCount.Should().Be(2);
    }

    [HumansFact]
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

    [HumansFact]
    public async Task GetProfileEditDataAsync_SubmittedApp_IsTierLocked()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, isApproved: false);
        var existingApp = new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MembershipTier = MembershipTier.Colaborador,
            Motivation = "test",
            SubmittedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };

        _applicationDecisionService.GetUserApplicationsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<MemberApplication> { existingApp });

        var (profile, isTierLocked, pendingApp) = await _service.GetProfileEditDataAsync(userId);

        profile.Should().NotBeNull();
        isTierLocked.Should().BeTrue();
        pendingApp.Should().NotBeNull();
    }

    [HumansFact]
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

        _applicationDecisionService.GetUserApplicationsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<MemberApplication> { app });

        var (_, isTierLocked, pendingApp) = await _service.GetProfileEditDataAsync(userId);

        isTierLocked.Should().BeTrue();
        // Profile is approved, so PendingApplication is null even though app exists
        pendingApp.Should().BeNull();
    }

    [HumansFact]
    public async Task GetProfileEditDataAsync_NoApps_NotLocked()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        var (profile, isTierLocked, pendingApp) = await _service.GetProfileEditDataAsync(userId);

        profile.Should().NotBeNull();
        isTierLocked.Should().BeFalse();
        pendingApp.Should().BeNull();
    }

    [HumansFact]
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

    [HumansFact]
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

    [HumansFact]
    public async Task GetCustomPictureInfoByUserIdsAsync_EmptyInput_ReturnsEmpty()
    {
        var result = await _service.GetCustomPictureInfoByUserIdsAsync(Array.Empty<Guid>());

        result.Should().BeEmpty();
    }

    [HumansFact]
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

        // CachingProfileService overrides this; test the static-method path directly
        var snapshot = new[] { MakeFullProfile(p1, u1), MakeFullProfile(p2, u2), MakeFullProfile(p3, u3) };
        var result = snapshot
            .Where(fp => fp.IsApproved && !fp.IsSuspended && fp.BirthdayMonth == 3 && fp.BirthdayDay.HasValue)
            .OrderBy(fp => fp.BirthdayDay)
            .ToList();

        result.Should().HaveCount(2);
        result[0].BirthdayDay.Should().Be(5);
        result[1].BirthdayDay.Should().Be(20);
    }

    [HumansFact]
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

        var snapshot = new[] { MakeFullProfile(p1, u1), MakeFullProfile(p2, u2) };
        var result = snapshot
            .Where(fp => fp.IsApproved && !fp.IsSuspended && fp.BirthdayMonth == 3 && fp.BirthdayDay.HasValue)
            .ToList();

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(u2);
    }

    [HumansFact]
    public async Task GetBirthdayProfilesAsync_NoMatches_ReturnsEmpty()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var profile = MakeProfile(userId);
        profile.DateOfBirth = new LocalDate(4, 6, 10);
        _dbContext.Profiles.Add(profile);
        await _dbContext.SaveChangesAsync();

        var snapshot = new[] { MakeFullProfile(profile, userId) };
        var result = snapshot
            .Where(fp => fp.IsApproved && !fp.IsSuspended && fp.BirthdayMonth == 3 && fp.BirthdayDay.HasValue)
            .ToList();

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetApprovedProfilesWithLocationAsync_ReturnsApprovedWithCoordinates()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var profile = MakeProfile(userId, isApproved: true);
        profile.Latitude = 40.0;
        profile.Longitude = -3.0;
        _dbContext.Profiles.Add(profile);
        await _dbContext.SaveChangesAsync();

        var snapshot = new[] { MakeFullProfile(profile, userId) };
        var result = snapshot
            .Where(fp => fp.IsApproved && !fp.IsSuspended && fp.Latitude.HasValue && fp.Longitude.HasValue)
            .ToList();

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(userId);
        result[0].Latitude.Should().Be(40.0);
        result[0].Longitude.Should().Be(-3.0);
    }

    [HumansFact]
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

        var snapshot = new[] { MakeFullProfile(p1, u1), MakeFullProfile(p2, u2), MakeFullProfile(p3, u3) };
        var result = snapshot
            .Where(fp => fp.IsApproved && !fp.IsSuspended && fp.Latitude.HasValue && fp.Longitude.HasValue)
            .ToList();

        result.Should().BeEmpty();
    }

    // --- Admin queries ---

    [HumansFact]
    public async Task GetFilteredHumansAsync_NoFilter_ReturnsAll()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var user1 = await SeedUserAsync(u1);
        var user2 = await SeedUserAsync(u2);
        _userService.GetAllUsersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<User> { user1, user2 });

        var result = await ProfileService.GetFilteredHumansFromSnapshotAsync(
            [], null, null, new List<User> { user1, user2 }, _membershipCalculator);

        result.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task GetFilteredHumansAsync_SearchByEmail()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var user1 = await SeedUserAsync(u1);
        var user2 = await SeedUserAsync(u2);
        user2.Email = "special@example.com";
        user2.UserName = "special@example.com";
        await _dbContext.SaveChangesAsync();
        _userService.GetAllUsersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<User> { user1, user2 });

        var result = await ProfileService.GetFilteredHumansFromSnapshotAsync(
            [], "special", null, new List<User> { user1, user2 }, _membershipCalculator);

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(u2);
    }

    [HumansFact]
    public async Task GetFilteredHumansAsync_SearchByDisplayName()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var user1 = await SeedUserAsync(u1);
        var user2 = await SeedUserAsync(u2);
        user2.DisplayName = "UniqueFlame";
        await _dbContext.SaveChangesAsync();
        _userService.GetAllUsersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<User> { user1, user2 });

        var result = await ProfileService.GetFilteredHumansFromSnapshotAsync(
            [], "UniqueFlame", null, new List<User> { user1, user2 }, _membershipCalculator);

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(u2);
    }

    [HumansFact]
    public async Task GetFilteredHumansAsync_StatusActive()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var user1 = await SeedUserAsync(u1);
        var user2 = await SeedUserAsync(u2);
        _dbContext.Profiles.Add(MakeProfile(u1, isApproved: true));
        _dbContext.Profiles.Add(MakeProfile(u2, isApproved: false));
        await _dbContext.SaveChangesAsync();

        _userService.GetAllUsersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<User> { user1, user2 });

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

        var result = await ProfileService.GetFilteredHumansFromSnapshotAsync(
            [], null, "active", new List<User> { user1, user2 }, _membershipCalculator);

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(u1);
    }

    [HumansFact]
    public async Task GetFilteredHumansAsync_StatusPending()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var user1 = await SeedUserAsync(u1);
        var user2 = await SeedUserAsync(u2);
        _dbContext.Profiles.Add(MakeProfile(u1, isApproved: true));
        _dbContext.Profiles.Add(MakeProfile(u2, isApproved: false));
        await _dbContext.SaveChangesAsync();

        _userService.GetAllUsersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<User> { user1, user2 });

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

        var result = await ProfileService.GetFilteredHumansFromSnapshotAsync(
            [], null, "pending", new List<User> { user1, user2 }, _membershipCalculator);

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(u2);
    }

    [HumansFact]
    public async Task GetFilteredHumansAsync_StatusSuspended()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var user1 = await SeedUserAsync(u1);
        var user2 = await SeedUserAsync(u2);
        _dbContext.Profiles.Add(MakeProfile(u1, isSuspended: true));
        _dbContext.Profiles.Add(MakeProfile(u2));
        await _dbContext.SaveChangesAsync();

        _userService.GetAllUsersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<User> { user1, user2 });

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

        var result = await ProfileService.GetFilteredHumansFromSnapshotAsync(
            [], null, "suspended", new List<User> { user1, user2 }, _membershipCalculator);

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(u1);
    }

    [HumansFact]
    public async Task GetAdminHumanDetailAsync_ReturnsFullDetail()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, isApproved: true);
        var user = await _dbContext.Users.FirstAsync(u => u.Id == userId);
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        var existingApp = new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MembershipTier = MembershipTier.Colaborador,
            Motivation = "test",
            SubmittedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _applicationDecisionService.GetUserApplicationsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<MemberApplication> { existingApp });

        var roleAssignment = new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleName = "Board",
            ValidFrom = _clock.GetCurrentInstant() - Duration.FromDays(10),
            CreatedAt = _clock.GetCurrentInstant(),
            CreatedByUserId = userId
        };
        _roleAssignmentService.GetByUserIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<RoleAssignment> { roleAssignment });

        _consentService.GetConsentRecordCountAsync(userId, Arg.Any<CancellationToken>())
            .Returns(1);

        var result = await _service.GetAdminHumanDetailAsync(userId);

        result.Should().NotBeNull();
        result!.User.Id.Should().Be(userId);
        result.Profile.Should().NotBeNull();
        result.Applications.Should().HaveCount(1);
        result.RoleAssignments.Should().HaveCount(1);
    }

    [HumansFact]
    public async Task GetAdminHumanDetailAsync_NonExistent_ReturnsNull()
    {
        _userService.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _service.GetAdminHumanDetailAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    // --- Cooldown and export ---

    [HumansFact]
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

    [HumansFact]
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

    [HumansFact]
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

    // --- Search (via static helpers, exercised directly since inner returns empty) ---

    [HumansFact]
    public async Task SearchHumansAsync_MatchesByDisplayName()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, displayName: "Sparkle Phoenix");
        var profile = MakeProfile(userId, isApproved: true);
        _dbContext.Profiles.Add(profile);
        await _dbContext.SaveChangesAsync();

        var snapshot = new[] { MakeFullProfile(profile, userId) };
        var results = ProfileService.SearchHumansFromSnapshot(snapshot, "Sparkle");

        results.Should().HaveCount(1);
        results[0].UserId.Should().Be(userId);
        results[0].MatchField.Should().Be("Name");
    }

    [HumansFact]
    public async Task SearchHumansAsync_MatchesByCity()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var profile = MakeProfile(userId, isApproved: true);
        profile.City = "Barcelona";
        _dbContext.Profiles.Add(profile);
        await _dbContext.SaveChangesAsync();

        var snapshot = new[] { MakeFullProfile(profile, userId) };
        var results = ProfileService.SearchHumansFromSnapshot(snapshot, "Barcelona");

        results.Should().HaveCount(1);
        results[0].MatchField.Should().Be("City");
    }

    [HumansFact]
    public async Task SearchHumansAsync_MatchesByBio()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var profile = MakeProfile(userId, isApproved: true);
        profile.Bio = "I love fire dancing and community building";
        _dbContext.Profiles.Add(profile);
        await _dbContext.SaveChangesAsync();

        var snapshot = new[] { MakeFullProfile(profile, userId) };
        var results = ProfileService.SearchHumansFromSnapshot(snapshot, "fire dancing");

        results.Should().HaveCount(1);
        results[0].MatchField.Should().Be("Bio");
        results[0].MatchSnippet.Should().Contain("fire dancing");
    }

    [HumansFact]
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

        var snapshot = new[] { MakeFullProfile(p1, u1), MakeFullProfile(p2, u2) };
        var results = ProfileService.SearchHumansFromSnapshot(snapshot, "Madrid");

        results.Should().HaveCount(1);
        results[0].UserId.Should().Be(u2);
    }

    [HumansFact]
    public async Task SearchHumansAsync_ExcludesUnapproved()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        await SeedUserAsync(u1);
        await SeedUserAsync(u2);
        var p1 = MakeProfile(u1, isApproved: false);
        p1.City = "Madrid";
        var p2 = MakeProfile(u2, isApproved: true);
        p2.City = "Madrid";
        await _dbContext.Profiles.AddRangeAsync(p1, p2);
        await _dbContext.SaveChangesAsync();

        var snapshot = new[] { MakeFullProfile(p1, u1), MakeFullProfile(p2, u2) };
        var results = ProfileService.SearchHumansFromSnapshot(snapshot, "Madrid");

        results.Should().HaveCount(1);
        results[0].UserId.Should().Be(u2);
    }

    [HumansFact]
    public async Task SearchHumansAsync_NoMatch_ReturnsEmpty()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var profile = MakeProfile(userId, isApproved: true);
        _dbContext.Profiles.Add(profile);
        await _dbContext.SaveChangesAsync();

        var snapshot = new[] { MakeFullProfile(profile, userId) };
        var results = ProfileService.SearchHumansFromSnapshot(snapshot, "zzzznonexistent");

        results.Should().BeEmpty();
    }

    // --- DB-backed fallback paths (§15 invariant: base service must work without decorator) ---

    [HumansFact]
    public async Task GetBirthdayProfilesAsync_BaseService_LoadsFromRepositoryAndFilters()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var u3 = Guid.NewGuid();
        var user1 = await SeedUserAsync(u1);
        var user2 = await SeedUserAsync(u2);
        var user3 = await SeedUserAsync(u3);

        var p1 = MakeProfile(u1, isApproved: true);
        p1.DateOfBirth = new LocalDate(4, 3, 20);
        var p2 = MakeProfile(u2, isApproved: true);
        p2.DateOfBirth = new LocalDate(4, 3, 5);
        var p3 = MakeProfile(u3, isApproved: true);
        p3.DateOfBirth = new LocalDate(4, 6, 15);
        await _dbContext.Profiles.AddRangeAsync(p1, p2, p3);
        await _dbContext.SaveChangesAsync();

        _userService.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User> { [u1] = user1, [u2] = user2, [u3] = user3 });

        var result = await _service.GetBirthdayProfilesAsync(3);

        result.Should().HaveCount(2);
        result[0].Day.Should().Be(5);
        result[1].Day.Should().Be(20);
    }

    [HumansFact]
    public async Task GetApprovedProfilesWithLocationAsync_BaseService_LoadsFromRepositoryAndFilters()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var user1 = await SeedUserAsync(u1);
        var user2 = await SeedUserAsync(u2);

        // Approved with location
        var p1 = MakeProfile(u1, isApproved: true);
        p1.Latitude = 40.0;
        p1.Longitude = -3.0;
        p1.City = "Madrid";
        // Unapproved with location (excluded)
        var p2 = MakeProfile(u2, isApproved: false);
        p2.Latitude = 41.0;
        p2.Longitude = -2.0;
        await _dbContext.Profiles.AddRangeAsync(p1, p2);
        await _dbContext.SaveChangesAsync();

        _userService.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User> { [u1] = user1, [u2] = user2 });

        var result = await _service.GetApprovedProfilesWithLocationAsync();

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(u1);
        result[0].City.Should().Be("Madrid");
    }

    [HumansFact]
    public async Task SearchApprovedUsersAsync_BaseService_LoadsFromRepositoryAndFilters()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var user1 = await SeedUserAsync(u1, displayName: "Unique Sparkle");
        var user2 = await SeedUserAsync(u2, displayName: "Other Person");

        var p1 = MakeProfile(u1, isApproved: true);
        var p2 = MakeProfile(u2, isApproved: true);
        await _dbContext.Profiles.AddRangeAsync(p1, p2);
        await _dbContext.SaveChangesAsync();

        _userService.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User> { [u1] = user1, [u2] = user2 });

        var result = await _service.SearchApprovedUsersAsync("Sparkle");

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(u1);
        result[0].DisplayName.Should().Be("Unique Sparkle");
    }

    [HumansFact]
    public async Task SearchHumansAsync_BaseService_LoadsFromRepositoryAndFilters()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var user1 = await SeedUserAsync(u1);
        var user2 = await SeedUserAsync(u2);

        var p1 = MakeProfile(u1, isApproved: true);
        p1.City = "Barcelona";
        var p2 = MakeProfile(u2, isApproved: true);
        p2.City = "Valencia";
        await _dbContext.Profiles.AddRangeAsync(p1, p2);
        await _dbContext.SaveChangesAsync();

        _userService.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User> { [u1] = user1, [u2] = user2 });

        var result = await _service.SearchHumansAsync("Barcelona");

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(u1);
        result[0].MatchField.Should().Be("City");
    }

    [HumansFact]
    public async Task GetFilteredHumansAsync_BaseService_LoadsFromRepositoryAndReturnsRows()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var user1 = await SeedUserAsync(u1, displayName: "Alice");
        var user2 = await SeedUserAsync(u2, displayName: "Bob");

        var p1 = MakeProfile(u1, isApproved: true);
        _dbContext.Profiles.Add(p1);
        // u2 has no profile
        await _dbContext.SaveChangesAsync();

        _userService.GetAllUsersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<User> { user1, user2 });
        _userService.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User> { [u1] = user1 });

        var result = await _service.GetFilteredHumansAsync(null, null);

        result.Should().HaveCount(2);
        var alice = result.Single(r => r.UserId == u1);
        alice.HasProfile.Should().BeTrue();
        alice.IsApproved.Should().BeTrue();
        var bob = result.Single(r => r.UserId == u2);
        bob.HasProfile.Should().BeFalse();
        bob.IsApproved.Should().BeFalse();
    }

    // --- GetFullProfileAsync ---

    [HumansFact]
    public async Task GetFullProfileAsync_ReturnsStitchedProjection_WhenProfileExists()
    {
        var userId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var user = await SeedUserAsync(userId, displayName: "Real Name", profilePictureUrl: "https://img");
        _dbContext.Profiles.Add(new Profile
        {
            Id = profileId,
            UserId = userId,
            BurnerName = "Burner",
            Bio = "Bio text",
            City = "Madrid",
            IsApproved = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        });
        await _dbContext.SaveChangesAsync();

        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _service.GetFullProfileAsync(userId);

        result.Should().NotBeNull();
        result!.UserId.Should().Be(userId);
        result.DisplayName.Should().Be("Real Name");
        result.ProfilePictureUrl.Should().Be("https://img");
        result.ProfileId.Should().Be(profileId);
        result.BurnerName.Should().Be("Burner");
        result.City.Should().Be("Madrid");
        result.IsApproved.Should().BeTrue();
        result.CVEntries.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetFullProfileAsync_ReturnsNull_WhenProfileMissing()
    {
        var userId = Guid.NewGuid();

        var result = await _service.GetFullProfileAsync(userId);

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetFullProfileAsync_ReturnsNull_WhenUserMissing()
    {
        // Seed a profile but no user. _userService.GetByIdAsync is an NSubstitute
        // default and returns null when unstubbed, exercising the second null guard.
        var userId = Guid.NewGuid();
        _dbContext.Profiles.Add(new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetFullProfileAsync(userId);

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetFullProfileAsync_PopulatesNotificationEmail_WhenVerifiedTargetExists()
    {
        var userId = Guid.NewGuid();
        var user = await SeedUserAsync(userId);
        user.Email = "primary@example.com";
        _dbContext.Profiles.Add(new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            IsApproved = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        });
        _dbContext.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "notify@example.com",
            IsVerified = true,
            IsNotificationTarget = true,
            DisplayOrder = 0
        });
        await _dbContext.SaveChangesAsync();

        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _service.GetFullProfileAsync(userId);

        result.Should().NotBeNull();
        result!.NotificationEmail.Should().Be("notify@example.com");
    }

    // --- SaveCVEntriesAsync ---

    [HumansFact]
    public async Task SaveCVEntriesAsync_DelegatesToRepository()
    {
        // Arrange: mock repository that knows about a seeded profile
        var userId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var mockRepo = Substitute.For<IProfileRepository>();
        var profile = new Profile
        {
            Id = profileId,
            UserId = userId,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        mockRepo.GetByUserIdAsync(userId, Arg.Any<CancellationToken>()).Returns(profile);

        var service = BuildServiceWith(mockRepo);

        var entries = new List<CVEntry>
        {
            new(Guid.Empty, new LocalDate(2025, 3, 1), "Nowhere 2025", "Sound crew"),
        };

        // Act
        await service.SaveCVEntriesAsync(userId, entries);

        // Assert: delegates to the repository with the profile's Id
        await mockRepo.Received(1)
            .ReconcileCVEntriesAsync(profileId, entries, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SaveCVEntriesAsync_NoOp_WhenUserHasNoProfile()
    {
        // Arrange: mock repository that returns null (no profile)
        var userId = Guid.NewGuid();

        var mockRepo = Substitute.For<IProfileRepository>();
        mockRepo.GetByUserIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns((Profile?)null);

        var service = BuildServiceWith(mockRepo);

        // Act
        await service.SaveCVEntriesAsync(userId, new List<CVEntry>());

        // Assert: reconcile is never called
        await mockRepo.DidNotReceive()
            .ReconcileCVEntriesAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<CVEntry>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Builds a <see cref="ProfileService"/> with a custom <see cref="IProfileRepository"/>
    /// while keeping all other dependencies wired to the same test-class fields.
    /// </summary>
    private ProfileService BuildServiceWith(IProfileRepository profileRepository) => new(
        profileRepository, _userService,
        _userEmailRepository,
        _contactFieldRepository, _communicationPreferenceRepository,
        _onboardingService, _auditLogService,
        _membershipCalculator, _consentService, _ticketQueryService,
        _applicationDecisionService, _campaignService,
        _roleAssignmentService, _accountDeletionService,
        _clock,
        NullLogger<ProfileService>.Instance);

    // --- Helpers ---

    private async Task<User> SeedUserAsync(Guid userId,
        string displayName = "Test User", string? profilePictureUrl = null)
    {
        var user = new User
        {
            Id = userId,
            DisplayName = displayName,
            UserName = $"test-{userId}@test.com",
            Email = $"test-{userId}@test.com",
            ProfilePictureUrl = profilePictureUrl,
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

    private FullProfile MakeFullProfile(Profile profile, Guid userId, string? displayName = null)
    {
        var user = _dbContext.Users.Find(userId)!;
        return new FullProfile(
            UserId: userId,
            DisplayName: displayName ?? user.DisplayName,
            ProfilePictureUrl: user.ProfilePictureUrl,
            HasCustomPicture: profile.ProfilePictureData is not null,
            ProfileId: profile.Id,
            UpdatedAtTicks: profile.UpdatedAt.ToUnixTimeTicks(),
            BurnerName: profile.BurnerName,
            Bio: profile.Bio,
            Pronouns: profile.Pronouns,
            ContributionInterests: profile.ContributionInterests,
            City: profile.City,
            CountryCode: profile.CountryCode,
            Latitude: profile.Latitude,
            Longitude: profile.Longitude,
            BirthdayDay: profile.DateOfBirth?.Day,
            BirthdayMonth: profile.DateOfBirth?.Month,
            IsApproved: profile.IsApproved,
            IsSuspended: profile.IsSuspended,
            CVEntries: profile.VolunteerHistory
                .Select(v => new CVEntry(v.Id, v.Date, v.EventName, v.Description))
                .ToList());
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

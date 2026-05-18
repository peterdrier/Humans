using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Xunit;
using ProfileService = Humans.Application.Services.Profiles.ProfileService;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Tests.Infrastructure;
using Humans.Infrastructure.Repositories.Profiles;

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
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly InMemoryFileStorage _fileStorage = new();

    // Delegate to the production helper (made internal for test access)
    // so the test can't drift from the real key construction.
    private static string PicKey(Guid profileId, string contentType) =>
        ProfileService.ProfilePictureKey(profileId, contentType);

    public ProfileServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));

        // Real repositories backed by an IDbContextFactory wrapping the in-memory store.
        var factory = new TestDbContextFactory(options);
        _profileRepository = new ProfileRepository(factory, _clock);
        _userEmailRepository = new UserEmailRepository(factory);
        _contactFieldRepository = new ContactFieldRepository(factory);

        _service = new ProfileService(
            _profileRepository, _userService,
            _userEmailRepository,
            _contactFieldRepository, _communicationPreferenceRepository,
            _auditLogService,
            _fileStorage,
            Substitute.For<IUserInfoInvalidator>(),
            _clock,
            NullLogger<ProfileService>.Instance);

        _userService.StubGetUserInfosFromContext(_dbContext);
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

    /// <summary>
    /// Issue #635 (§15i): the Stub → Active transition. SaveProfileAsync that
    /// populates BurnerName / FirstName / LastName promotes a freshly created
    /// Profile from <see cref="ProfileState.Stub"/> to
    /// <see cref="ProfileState.Active"/>.
    /// </summary>
    [HumansFact(Timeout = 10000)]
    public async Task ProfileService_UpdateProfileAsync_TransitionsStubToActive_WhenAllRequiredFieldsPopulated()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var request = MakeRequest(burnerName: "Flame", firstName: "Jane", lastName: "Doe");

        await _service.SaveProfileAsync(userId, "Jane Doe", request, "en");

        var profile = await _dbContext.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.State.Should().Be(ProfileState.Active);
    }

    /// <summary>
    /// Issue #635 (§15i): missing required fields keeps the Profile in Stub.
    /// </summary>
    [HumansFact(Timeout = 10000)]
    public async Task ProfileService_UpdateProfileAsync_StaysStub_WhenRequiredFieldsBlank()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        // BurnerName/FirstName/LastName all empty — Stub state.
        var request = MakeRequest(burnerName: "", firstName: "", lastName: "");

        await _service.SaveProfileAsync(userId, "Stub", request, "en");

        var profile = await _dbContext.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.State.Should().Be(ProfileState.Stub);
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

    // --- Profile picture write paths (file share is the source of truth; the
    // DB bytes column is obsolete and untouched by code) ---

    [HumansFact]
    public async Task SaveProfileAsync_UploadsProfilePicture_WritesToFilesystem()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        var payload = new byte[] { 0x10, 0x20, 0x30 };
        var request = MakeRequest(pictureData: payload, pictureContentType: "image/jpeg");

        await _service.SaveProfileAsync(userId, "Test", request, "en");

        var profile = await _dbContext.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        // Content-type column is the "has picture" marker + extension source.
        profile.ProfilePictureContentType.Should().Be("image/jpeg");
        // Bytes live on the file share, keyed under uploads/profile-pictures/.
        var key = PicKey(profile.Id, "image/jpeg");
        _fileStorage.Files.Should().ContainKey(key);
        _fileStorage.Files[key].Should().BeEquivalentTo(payload);
    }

    [HumansFact]
    public async Task SaveProfileAsync_RemoveProfilePicture_ClearsContentTypeAndDeletesFile()
    {
        var userId = Guid.NewGuid();
        var profileId = await SeedUserWithProfileAsync(userId, withPicture: true);

        var request = MakeRequest(removeProfilePicture: true);
        await _service.SaveProfileAsync(userId, "Test", request, "en");

        var profile = await _dbContext.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.ProfilePictureContentType.Should().BeNull();
        _fileStorage.Files.Should().NotContainKey(PicKey(profileId, "image/png"));
    }

    // Threshold check (formerly SaveProfileAsync_CallsSetConsentCheckPending)
    // moved out of ProfileService entirely — it's a director method on
    // IOnboardingService now, invoked by controllers as a peer call after
    // SaveProfileAsync. ProfileService has no dep on Onboarding.

    // --- Profile save flow: tier application during initial setup ---

    // Tier-application orchestration moved to ProfileController.Edit POST in
    // issue nobodies-collective/Humans#685. Deletion request/cancel moved to
    // IAccountDeletionService (covered by AccountDeletionServiceTests).

    [HumansFact]
    public async Task GetProfilePictureAsync_WithPicture_ReturnsData()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, withPicture: true);
        var profile = await _dbContext.Profiles.FirstAsync(p => p.UserId == userId);

        var result = await _service.GetProfilePictureAsync(profile.Id);

        result.Should().NotBeNull();
        result!.Value.Data.Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
        result.Value.ContentType.Should().Be("image/png");
    }

    [HumansFact]
    public async Task GetProfilePictureAsync_NoPicture_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, withPicture: false);
        var profile = await _dbContext.Profiles.FirstAsync(p => p.UserId == userId);

        var result = await _service.GetProfilePictureAsync(profile.Id);

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetProfilePictureAsync_NoProfile_ReturnsNull()
    {
        var result = await _service.GetProfilePictureAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetProfilePictureAsync_FilesystemHit_ServesFromStoreWithoutDbBytes()
    {
        // FS-first fast path: when the picture is already on disk we should
        // serve those bytes directly even if they differ from the DB copy.
        // This pins the read path to the store as the authoritative source
        // when the DB content-type column says a picture exists.
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, withPicture: true);
        var profile = await _dbContext.Profiles.FirstAsync(p => p.UserId == userId);

        var fsPayload = new byte[] { 9, 9, 9, 9 };
        await _fileStorage.SaveAsync(PicKey(profile.Id, "image/png"), fsPayload);

        var result = await _service.GetProfilePictureAsync(profile.Id);

        result.Should().NotBeNull();
        result!.Value.Data.Should().BeEquivalentTo(fsPayload);
        result.Value.ContentType.Should().Be("image/png");
    }

    [HumansFact]
    public async Task GetProfilePictureAsync_FilesystemMiss_ReturnsNull()
    {
        // Content-type gate passes but the file is gone — the picture is
        // simply missing. (Pre-cleanup PR a DB-bytes fallback masked this;
        // the file share is now the only source of truth.)
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, withPicture: true);
        var profile = await _dbContext.Profiles.FirstAsync(p => p.UserId == userId);

        await _fileStorage.DeleteAsync(PicKey(profile.Id, "image/png"));

        var result = await _service.GetProfilePictureAsync(profile.Id);

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetProfilePictureAsync_AnonymizedProfile_ReturnsNullEvenWithStaleFile()
    {
        // GDPR: after anonymization the content-type column is null. If the
        // on-disk file wasn't successfully removed (best-effort delete failed)
        // the read path MUST NOT serve it. The content-type column is the gate.
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, withPicture: true);
        var profile = await _dbContext.Profiles.FirstAsync(p => p.UserId == userId);

        // Clear the gate as if anonymization had run.
        var tracked = await _dbContext.Profiles.FirstAsync(p => p.UserId == userId);
        tracked.ProfilePictureContentType = null;
        await _dbContext.SaveChangesAsync();

        // Confirm the stale file is still on disk to make the gate meaningful.
        _fileStorage.Files.ContainsKey(PicKey(profile.Id, "image/png")).Should().BeTrue();

        var result = await _service.GetProfilePictureAsync(profile.Id);

        result.Should().BeNull("DB content-type is null after anonymization, so the stale on-disk file must not be served");
    }

    // Profile-index/edit/admin-detail bundling moved to ProfileController in
    // issue nobodies-collective/Humans#685 — composition is now controller
    // concern (Profile + Application data are fetched separately and assembled
    // for the view). No ProfileService methods to test here.

    // Birthday/Location snapshot tests removed alongside the FullProfile delete —
    // those widgets now read directly from the UserInfo cache via CachingUserService.

    // GetAdminHumanDetailAsync moved to ProfileController.AdminDetail in
    // issue nobodies-collective/Humans#685 — composition is now controller
    // concern.

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
        });
        await _dbContext.SaveChangesAsync();

        var (canAdd, minutesUntilResend, pendingEmailId) = await _service.GetEmailCooldownInfoAsync(emailId);

        canAdd.Should().BeTrue();
        minutesUntilResend.Should().Be(0);
        pendingEmailId.Should().BeNull();
    }

    // SearchProfilesAsync + GetFullProfileAsync tests removed alongside the
    // FullProfile delete. The search surface lives on IUserService.SearchUsersAsync
    // and is covered by CachingUserServiceTests.

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
        _auditLogService,
        _fileStorage,
        Substitute.For<IUserInfoInvalidator>(),
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

    private async Task<Guid> SeedUserWithProfileAsync(Guid userId,
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
            profile.ProfilePictureContentType = "image/png";
        }
        _dbContext.Profiles.Add(profile);
        await _dbContext.SaveChangesAsync();
        if (withPicture)
        {
            await _fileStorage.SaveAsync(PicKey(profile.Id, "image/png"), [1, 2, 3]);
        }
        return profile.Id;
    }

    private static ProfileSaveRequest MakeRequest(
        string burnerName = "TestBurner", string firstName = "Test", string lastName = "User",
        int? birthdayMonth = null, int? birthdayDay = null,
        bool removeProfilePicture = false,
        byte[]? pictureData = null, string? pictureContentType = null,
        string? city = null)
    {
        return new ProfileSaveRequest(
            BurnerName: burnerName, FirstName: firstName, LastName: lastName,
            City: city, CountryCode: null, Latitude: null, Longitude: null, PlaceId: null,
            Bio: null, Pronouns: null, ContributionInterests: null, BoardNotes: null,
            BirthdayMonth: birthdayMonth, BirthdayDay: birthdayDay,
            EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelationship: null,
            NoPriorBurnExperience: false,
            ProfilePictureData: pictureData, ProfilePictureContentType: pictureContentType,
            RemoveProfilePicture: removeProfilePicture);
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

    [HumansFact]
    public async Task ContributeForUserAsync_EmitsIsOAuthKey_SourcedFromProviderColumn()
    {
        // The JSON key stays "IsOAuth" per memory/code/no-rename-serialized-fields.md
        // (exports are JSON files users download). The value sources from
        // (Provider != null) — pre-PR-4 semantics meaning "this row has an OAuth
        // login attached". The PR 4 spec's Task 17 swapped both the JSON key and
        // the value source (e.IsGoogle); both have been reverted so the export
        // emits identical bytes for the same row data as before PR 4.
        //
        // This row has Provider="Google" and IsGoogle=false to pin the source:
        // under the reverted (Provider != null) projection it emits IsOAuth=true,
        // which proves the source is Provider, not IsGoogle.
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        _dbContext.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "g@example.com",
            IsVerified = true,
            IsPrimary = true,
            Provider = "Google",
            ProviderKey = "sub-1",
            IsGoogle = false,
        });
        await _dbContext.SaveChangesAsync();

        var slices = await _service.ContributeForUserAsync(userId, CancellationToken.None);

        var userEmailsSlice = slices.Single(s =>
            string.Equals(s.SectionName, Interfaces.Gdpr.GdprExportSections.UserEmails, StringComparison.Ordinal));
        var json = System.Text.Json.JsonSerializer.Serialize(userEmailsSlice.Data);
        json.Should().Contain("\"IsOAuth\":true");
        // Legacy JSON key preserved for the C# IsPrimary rename
        // (memory/code/no-rename-serialized-fields.md). Mirrors EF's HasColumnName pin on the
        // renamed property — the GDPR export must keep emitting "IsNotificationTarget".
        json.Should().Contain("\"IsNotificationTarget\":true");
        json.Should().NotContain("\"IsPrimary\":");
    }

    // Smoke test for the per-userId service-side lock that replaced the
    // AddIfNotExistsByUserIdAsync 23505-translating repo method. Asserts the
    // lock serializes two concurrent EnsureStubProfileAsync callers so that
    // only one AddAsync fires for a given userId.
    [HumansFact(Timeout = 5000)]
    public async Task EnsureStubProfileAsync_TwoConcurrentCallers_OnlyOneAddAsync()
    {
        var userId = Guid.NewGuid();

        var fakeRepo = Substitute.For<IProfileRepository>();
        Profile? stored = null;
        fakeRepo.GetByUserIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(stored));
        fakeRepo.When(r => r.AddAsync(Arg.Any<Profile>(), Arg.Any<CancellationToken>()))
            .Do(call => stored = call.Arg<Profile>());

        var service = new ProfileService(
            fakeRepo, _userService,
            _userEmailRepository,
            _contactFieldRepository, _communicationPreferenceRepository,
            _auditLogService,
            _fileStorage,
            Substitute.For<IUserInfoInvalidator>(),
            _clock,
            NullLogger<ProfileService>.Instance);

#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
        await Task.WhenAll(
            Task.Run(() => service.EnsureStubProfileAsync(userId)),
            Task.Run(() => service.EnsureStubProfileAsync(userId)));
#pragma warning restore VSTHRD003

        await fakeRepo.Received(1).AddAsync(Arg.Any<Profile>(), Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // Issue #474 — phase 5 service-ownership: every profile state mutation now
    // routes through ProfileService so the §15 caching decorator can keep the
    // FullProfile dict in sync atomically with the DB write. These tests pin
    // the contracts the decorator relies on (return values, field updates,
    // error keys) so future refactors can't drift the behaviour.
    // ==========================================================================

    [HumansFact]
    public async Task SetMembershipTierAsync_UpdatesTierAndUpdatedAt()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        _clock.Advance(Duration.FromHours(1));

        await _service.SetMembershipTierAsync(userId, MembershipTier.Colaborador);

        var profile = await _dbContext.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.MembershipTier.Should().Be(MembershipTier.Colaborador);
        profile.UpdatedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task SetMembershipTierAsync_UnknownUser_NoOp()
    {
        var unknownUserId = Guid.NewGuid();

        // No throw, no profile created — just a logged warning.
        await _service.SetMembershipTierAsync(unknownUserId, MembershipTier.Asociado);

        var profileExists = await _dbContext.Profiles.AsNoTracking().AnyAsync(p => p.UserId == unknownUserId);
        profileExists.Should().BeFalse();
    }

    [HumansFact]
    public async Task RecordConsentCheckAsync_Cleared_FlipsIsApprovedAndStamps()
    {
        var userId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        var result = await _service.RecordConsentCheckAsync(
            userId, reviewerId, ConsentCheckStatus.Cleared, "Looks good");

        result.Success.Should().BeTrue();
        var profile = await _dbContext.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.ConsentCheckStatus.Should().Be(ConsentCheckStatus.Cleared);
        profile.IsApproved.Should().BeTrue();
        profile.ConsentCheckAt.Should().Be(_clock.GetCurrentInstant());
        profile.ConsentCheckedByUserId.Should().Be(reviewerId);
        profile.ConsentCheckNotes.Should().Be("Looks good");
    }

    [HumansFact]
    public async Task RecordConsentCheckAsync_Flagged_KeepsIsApprovedFalse()
    {
        var userId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        var result = await _service.RecordConsentCheckAsync(
            userId, reviewerId, ConsentCheckStatus.Flagged, "Concern X");

        result.Success.Should().BeTrue();
        var profile = await _dbContext.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.ConsentCheckStatus.Should().Be(ConsentCheckStatus.Flagged);
        profile.IsApproved.Should().BeFalse();
    }

    [HumansFact]
    public async Task RecordConsentCheckAsync_NoProfile_ReturnsNotFound()
    {
        var unknownUserId = Guid.NewGuid();

        var result = await _service.RecordConsentCheckAsync(
            unknownUserId, Guid.NewGuid(), ConsentCheckStatus.Cleared, null);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
    }

    [HumansFact]
    public async Task RecordConsentCheckAsync_AlreadyRejected_BlocksClear()
    {
        var userId = Guid.NewGuid();
        var profileId = await SeedUserWithProfileAsync(userId);
        var profile = await _dbContext.Profiles.FirstAsync(p => p.Id == profileId);
        profile.RejectedAt = _clock.GetCurrentInstant();
        await _dbContext.SaveChangesAsync();

        var result = await _service.RecordConsentCheckAsync(
            userId, Guid.NewGuid(), ConsentCheckStatus.Cleared, null);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("AlreadyRejected");
    }

    [HumansFact]
    public async Task RecordConsentCheckAsync_PendingValue_Throws()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        // Pending is the system-driven transition — must use SetConsentCheckPendingAsync.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.RecordConsentCheckAsync(
                userId, Guid.NewGuid(), ConsentCheckStatus.Pending, null));
    }

    [HumansFact]
    public async Task RejectSignupAsync_StampsRejectionFields()
    {
        var userId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        var result = await _service.RejectSignupAsync(userId, reviewerId, "Spam account");

        result.Success.Should().BeTrue();
        var profile = await _dbContext.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.RejectedAt.Should().Be(_clock.GetCurrentInstant());
        profile.RejectedByUserId.Should().Be(reviewerId);
        profile.RejectionReason.Should().Be("Spam account");
        profile.IsApproved.Should().BeFalse();
    }

    [HumansFact]
    public async Task RejectSignupAsync_AlreadyRejected_ReturnsErrorKey()
    {
        var userId = Guid.NewGuid();
        var profileId = await SeedUserWithProfileAsync(userId);
        var profile = await _dbContext.Profiles.FirstAsync(p => p.Id == profileId);
        profile.RejectedAt = _clock.GetCurrentInstant();
        await _dbContext.SaveChangesAsync();

        var result = await _service.RejectSignupAsync(userId, Guid.NewGuid(), null);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("AlreadyRejected");
    }

    [HumansFact]
    public async Task RejectSignupAsync_NoProfile_ReturnsNotFound()
    {
        var result = await _service.RejectSignupAsync(Guid.NewGuid(), Guid.NewGuid(), null);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
    }

    [HumansFact]
    public async Task ApproveVolunteerAsync_FlipsIsApprovedTrue()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, isApproved: false);

        var result = await _service.ApproveVolunteerAsync(userId, Guid.NewGuid());

        result.Success.Should().BeTrue();
        var profile = await _dbContext.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.IsApproved.Should().BeTrue();
        profile.UpdatedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task ApproveVolunteerAsync_NoProfile_ReturnsNotFound()
    {
        var result = await _service.ApproveVolunteerAsync(Guid.NewGuid(), Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
    }

    [HumansFact]
    public async Task SetSuspendedAsync_True_SetsSuspendedAndState()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        var result = await _service.SetSuspendedAsync(userId, Guid.NewGuid(), suspended: true, "Disruptive");

        result.Success.Should().BeTrue();
        var profile = await _dbContext.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
#pragma warning disable HUM_PROFILE_ISSUSPENDED
        profile.IsSuspended.Should().BeTrue();
#pragma warning restore HUM_PROFILE_ISSUSPENDED
        profile.State.Should().Be(ProfileState.Suspended);
        profile.AdminNotes.Should().Be("Disruptive");
    }

    [HumansFact]
    public async Task SetSuspendedAsync_FalseRestoresActiveWhenIdentityComplete()
    {
        var userId = Guid.NewGuid();
        var profileId = await SeedUserWithProfileAsync(userId);
        var profile = await _dbContext.Profiles.FirstAsync(p => p.Id == profileId);
#pragma warning disable HUM_PROFILE_ISSUSPENDED
        profile.IsSuspended = true;
#pragma warning restore HUM_PROFILE_ISSUSPENDED
        profile.State = ProfileState.Suspended;
        // SeedUserWithProfileAsync seeds BurnerName/FirstName/LastName, so identity is complete.
        await _dbContext.SaveChangesAsync();

        var result = await _service.SetSuspendedAsync(userId, Guid.NewGuid(), suspended: false, notes: null);

        result.Success.Should().BeTrue();
        var fresh = await _dbContext.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
#pragma warning disable HUM_PROFILE_ISSUSPENDED
        fresh.IsSuspended.Should().BeFalse();
#pragma warning restore HUM_PROFILE_ISSUSPENDED
        fresh.State.Should().Be(ProfileState.Active);
    }

    [HumansFact]
    public async Task SetSuspendedAsync_NoProfile_ReturnsNotFound()
    {
        var result = await _service.SetSuspendedAsync(
            Guid.NewGuid(), Guid.NewGuid(), suspended: true, notes: null);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
    }

    [HumansFact]
    public async Task SetConsentCheckPendingAsync_FlipsToPending()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        var set = await _service.SetConsentCheckPendingAsync(userId);

        set.Should().BeTrue();
        var profile = await _dbContext.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.ConsentCheckStatus.Should().Be(ConsentCheckStatus.Pending);
    }

    [HumansFact]
    public async Task SetConsentCheckPendingAsync_NoProfile_ReturnsFalse()
    {
        var set = await _service.SetConsentCheckPendingAsync(Guid.NewGuid());

        set.Should().BeFalse();
    }
}

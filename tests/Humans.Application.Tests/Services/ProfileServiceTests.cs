using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Xunit;
using ProfileService = Humans.Application.Services.Profiles.ProfileService;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Users;
using Humans.Application.Tests.Infrastructure;
using Humans.Infrastructure.Repositories.Profiles;
using Humans.Infrastructure.Repositories.Users;
using ProfileEditorService = Humans.Application.Services.Profiles.ProfileEditorService;
using ProfilePictureStorageKeys = Humans.Application.Services.Profiles.ProfilePictureStorageKeys;

namespace Humans.Application.Tests.Services;

public sealed class ProfileServiceTests : ServiceTestHarness
{
    private readonly ProfileService _service;
    private readonly ProfileEditorService _editor;
    private readonly IProfileRepository _profileRepository;
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserEmailRepository _userEmailRepository;
    private readonly IContactFieldRepository _contactFieldRepository;
    private readonly ICommunicationPreferenceRepository _communicationPreferenceRepository = Substitute.For<ICommunicationPreferenceRepository>();
    private readonly InMemoryFileStorage _fileStorage = new();

    // Delegate to the production helper (made internal for test access)
    // so the test can't drift from the real key construction.
    private static string PicKey(Guid profileId, string contentType) =>
        ProfilePictureStorageKeys.ProfilePictureKey(profileId, contentType);

    public ProfileServiceTests()
    {
        // Real repositories backed by an IDbContextFactory wrapping the in-memory store.
        _profileRepository = new ProfileRepository(DbFactory, Clock);
        _userEmailRepository = new UserEmailRepository(DbFactory);
        _contactFieldRepository = new ContactFieldRepository(DbFactory);
        var storageUserService = new UserService(
            new UserRepository(DbFactory),
            _userEmailRepository,
            _profileRepository,
            _contactFieldRepository,
            _communicationPreferenceRepository,
            AdminAuthorization,
            Clock,
            NullLogger<UserService>.Instance);

        _service = new ProfileService(
            _profileRepository, _userService,
            _fileStorage,
            NullLogger<ProfileService>.Instance);
        _editor = new ProfileEditorService(
            _userService,
            _fileStorage,
            NullLogger<ProfileEditorService>.Instance);

        _userService.StubGetUserInfosFromContext(Db);
        _userService.StubGetUserInfoFromContext(Db);
        _userService.SaveProfileAsync(
                Arg.Any<Guid>(),
                Arg.Any<UserProfileSaveCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(call => storageUserService.SaveProfileAsync(
                call.ArgAt<Guid>(0),
                call.ArgAt<UserProfileSaveCommand>(1),
                call.ArgAt<CancellationToken>(2)));
        _userService.SetProfilePictureContentTypeAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(call => storageUserService.SetProfilePictureContentTypeAsync(
                call.ArgAt<Guid>(0),
                call.ArgAt<string>(1),
                call.ArgAt<CancellationToken>(2)));
    }

    // --- Profile editor save flow ---

    [HumansFact(Timeout = 10000)]
    public async Task SaveProfileAsync_NewProfile_CreatesProfile()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var request = MakeRequest(burnerName: "Flame", firstName: "Jane", lastName: "Doe");

        var profileId = await _editor.SaveProfileAsync(userId, "Jane Doe", request);

        profileId.Should().NotBe(Guid.Empty);
        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
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
    public async Task ProfileEditorService_SaveProfileAsync_TransitionsStubToActive_WhenAllRequiredFieldsPopulated()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var request = MakeRequest(burnerName: "Flame", firstName: "Jane", lastName: "Doe");

        await _editor.SaveProfileAsync(userId, "Jane Doe", request);

        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.State.Should().Be(ProfileState.Active);
    }

    /// <summary>
    /// Issue #635 (§15i): missing required fields keeps the Profile in Stub.
    /// </summary>
    [HumansFact(Timeout = 10000)]
    public async Task ProfileEditorService_SaveProfileAsync_StaysStub_WhenRequiredFieldsBlank()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        // BurnerName/FirstName/LastName all empty — Stub state.
        var request = MakeRequest(burnerName: "", firstName: "", lastName: "");

        await _editor.SaveProfileAsync(userId, "Stub", request);

        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.State.Should().Be(ProfileState.Stub);
    }

    [HumansFact]
    public async Task SaveProfileAsync_ExistingProfile_UpdatesFields()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        var request = MakeRequest(burnerName: "NewName", firstName: "Updated", lastName: "Person");

        await _editor.SaveProfileAsync(userId, "Updated Person", request);

        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.BurnerName.Should().Be("NewName");
        profile.FirstName.Should().Be("Updated");
        profile.UpdatedAt.Should().Be(Clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task SaveProfileAsync_UpdatesUserDisplayName()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        var request = MakeRequest();

        await _editor.SaveProfileAsync(userId, "New Display Name", request);

        var user = await Db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        user.DisplayName.Should().Be("New Display Name");
    }

    [HumansFact]
    public async Task SaveProfileAsync_ParsesBirthday_ValidDate()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        var request = MakeRequest(birthdayMonth: 2, birthdayDay: 14);

        await _editor.SaveProfileAsync(userId, "Test", request);

        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.DateOfBirth.Should().Be(new LocalDate(4, 2, 14));
    }

    [HumansFact]
    public async Task SaveProfileAsync_ParsesBirthday_InvalidDay_SetsNull()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        var request = MakeRequest(birthdayMonth: 2, birthdayDay: 30);

        await _editor.SaveProfileAsync(userId, "Test", request);

        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
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

        await _editor.SaveProfileAsync(userId, "Test", request);

        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
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
        await _editor.SaveProfileAsync(userId, "Test", request);

        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
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
        var profile = await Db.Profiles.FirstAsync(p => p.UserId == userId);

        var result = await _service.GetProfilePictureAsync(profile.Id);

        result.Should().NotBeNull();
        result.Value.Data.Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
        result.Value.ContentType.Should().Be("image/png");
    }

    [HumansFact]
    public async Task GetProfilePictureAsync_NoPicture_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, withPicture: false);
        var profile = await Db.Profiles.FirstAsync(p => p.UserId == userId);

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
        var profile = await Db.Profiles.FirstAsync(p => p.UserId == userId);

        var fsPayload = new byte[] { 9, 9, 9, 9 };
        await _fileStorage.SaveAsync(PicKey(profile.Id, "image/png"), fsPayload);

        var result = await _service.GetProfilePictureAsync(profile.Id);

        result.Should().NotBeNull();
        result.Value.Data.Should().BeEquivalentTo(fsPayload);
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
        var profile = await Db.Profiles.FirstAsync(p => p.UserId == userId);

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
        var profile = await Db.Profiles.FirstAsync(p => p.UserId == userId);

        // Clear the gate as if anonymization had run.
        var tracked = await Db.Profiles.FirstAsync(p => p.UserId == userId);
        tracked.ProfilePictureContentType = null;
        await Db.SaveChangesAsync();

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

    // SearchProfilesAsync + GetFullProfileAsync tests removed alongside the
    // FullProfile delete. The search surface lives on IUserService.SearchUsersAsync
    // and is covered by CachingUserServiceTests.

    // --- SaveProfileVolunteerHistoryAsync ---

    [HumansFact]
    public async Task SaveProfileVolunteerHistoryAsync_DelegatesToRepository()
    {
        // Arrange: mock repository that knows about a seeded profile
        var userId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var mockRepo = Substitute.For<IProfileRepository>();
        var profile = new Profile
        {
            Id = profileId,
            UserId = userId,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        mockRepo.GetByUserIdAsync(userId, Arg.Any<CancellationToken>()).Returns(profile);

        var service = BuildUserServiceWith(mockRepo);

        var entries = new List<CVEntry>
        {
            new(Guid.Empty, new LocalDate(2025, 3, 1), "Nowhere 2025", "Sound crew"),
        };

        // Act
        var saved = await service.SaveProfileVolunteerHistoryAsync(userId, entries);

        // Assert: delegates to the repository with the profile's Id
        saved.Should().BeTrue();
        await mockRepo.Received(1)
            .ReconcileCVEntriesAsync(profileId, entries, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SaveProfileVolunteerHistoryAsync_ReturnsFalse_WhenUserHasNoProfile()
    {
        // Arrange: mock repository that returns null (no profile)
        var userId = Guid.NewGuid();

        var mockRepo = Substitute.For<IProfileRepository>();
        mockRepo.GetByUserIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns((Profile?)null);

        var service = BuildUserServiceWith(mockRepo);

        // Act
        var saved = await service.SaveProfileVolunteerHistoryAsync(userId, new List<CVEntry>());

        // Assert: reconcile is never called
        saved.Should().BeFalse();
        await mockRepo.DidNotReceive()
            .ReconcileCVEntriesAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<CVEntry>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Builds a <see cref="UserService"/> with a custom <see cref="IProfileRepository"/>
    /// while keeping all other dependencies wired to the same test-class fields.
    /// </summary>
    private UserService BuildUserServiceWith(IProfileRepository profileRepository) => new(
        new UserRepository(DbFactory),
        _userEmailRepository,
        profileRepository,
        _contactFieldRepository,
        _communicationPreferenceRepository,
        AdminAuthorization,
        Clock,
        NullLogger<UserService>.Instance);

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
        Db.Users.Add(user);
        await Db.SaveChangesAsync();
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
            CreatedAt = Clock.GetCurrentInstant() - Duration.FromDays(1),
            UpdatedAt = Clock.GetCurrentInstant() - Duration.FromDays(1)
        };
        if (withPicture)
        {
            profile.ProfilePictureContentType = "image/png";
        }
        Db.Profiles.Add(profile);
        await Db.SaveChangesAsync();
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
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
    }

}

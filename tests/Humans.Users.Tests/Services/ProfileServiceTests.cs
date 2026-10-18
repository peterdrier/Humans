using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Humans.Users.Data;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using ProfileService = Humans.Users.Services.ProfileService;
using Humans.Users.Services;
using Humans.Users.Tests.Infrastructure;
using Humans.Users.Data.Repositories;
using ProfileEditorService = Humans.Users.Services.ProfileEditorService;
using ProfilePictureStorageKeys = Humans.Users.Contracts.ProfilePictureStorageKeys;
using Humans.Users.Contracts;

namespace Humans.Users.Tests.Services;

public sealed class ProfileServiceTests : ServiceTestHarness
{
    private readonly ProfileService _service;
    private readonly ProfileEditorService _editor;
    private readonly IUserRepository _userRepository;
    private readonly IUserManagement _userService = Substitute.For<IUserManagement>();
    private readonly ICommunicationPreferenceRepository _communicationPreferenceRepository = Substitute.For<ICommunicationPreferenceRepository>();
    private readonly UserService _storageUserService;
    private readonly InMemoryFileStorage _fileStorage = new();
    private readonly IFileStorage _pictureStorage = Substitute.For<IFileStorage>();

    // Delegate to the production helper (made internal for test access)
    // so the test can't drift from the real key construction.
    private static string PicKey(Guid profileId, string contentType) =>
        ProfilePictureStorageKeys.ProfilePictureKey(profileId, contentType);

    public ProfileServiceTests()
    {
        _pictureStorage.SaveAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(call => _fileStorage.SaveAsync(call.ArgAt<string>(0), call.ArgAt<byte[]>(1), call.ArgAt<CancellationToken>(2)));
        _pictureStorage.SaveAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(call => _fileStorage.SaveAsync(call.ArgAt<string>(0), call.ArgAt<Stream>(1), call.ArgAt<CancellationToken>(2)));
        _pictureStorage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => _fileStorage.DeleteAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
        _pictureStorage.TryReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => _fileStorage.TryReadAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
        // Real repositories backed by an IDbContextFactory wrapping the in-memory store.
        _userRepository = new UserRepository(DbFactory, Clock);
        _storageUserService = new UserService(
            _userRepository,
            _communicationPreferenceRepository,
            AdminAuthorization,
            Substitute.For<IRoleAssignmentClaimsCacheInvalidator>(),
            _pictureStorage,
            Clock,
            NullLogger<UserService>.Instance, AuditLog);

        _service = new ProfileService(
            _userRepository, _userService,
            _pictureStorage);
        _editor = new ProfileEditorService(
            _userService,
            NullLogger<ProfileEditorService>.Instance);

        _userService.StubGetUserInfosFromContext(Db);
        _userService.StubGetUserInfoFromContext(Db);
        _userService.SaveProfileAsync(
                Arg.Any<Guid>(),
                Arg.Any<UserProfileSaveCommand>(),
                Arg.Any<byte[]?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IReadOnlyList<CVEntry>?>())
            .Returns(call => _storageUserService.SaveProfileAsync(
                call.ArgAt<Guid>(0),
                call.ArgAt<UserProfileSaveCommand>(1),
                call.ArgAt<byte[]?>(2),
                call.ArgAt<CancellationToken>(3),
                call.ArgAt<IReadOnlyList<CVEntry>?>(4)));
    }

    // --- Profile editor save flow ---

    [HumansFact(Timeout = 10000)]
    public async Task SaveProfileAsync_NewProfile_CreatesProfile()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var request = MakeRequest(burnerName: "Flame", firstName: "Jane", lastName: "Doe");

        var save = await _editor.SaveProfileAsync(userId, "Jane Doe", request, ct: Xunit.TestContext.Current.CancellationToken);

        save.Succeeded.Should().BeTrue();
        save.ProfileId.Should().NotBeNull().And.NotBe(Guid.Empty);
        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId, Xunit.TestContext.Current.CancellationToken);
        profile.BurnerName.Should().Be("Flame");
        profile.FirstName.Should().Be("Jane");
        profile.LastName.Should().Be("Doe");
    }

    /// <summary>
    /// The Bare → Active transition: SaveProfileAsync that populates
    /// BurnerName / FirstName / LastName promotes the user's stored state.
    /// </summary>
    [HumansFact(Timeout = 10000)]
    public async Task ProfileEditorService_SaveProfileAsync_TransitionsBareToActive_WhenAllRequiredFieldsPopulated()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var request = MakeRequest(burnerName: "Flame", firstName: "Jane", lastName: "Doe");

        await _editor.SaveProfileAsync(userId, "Jane Doe", request, ct: Xunit.TestContext.Current.CancellationToken);

        var user = await Db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, Xunit.TestContext.Current.CancellationToken);
        user.State.Should().Be(UserState.Active);
    }

    /// <summary>Missing required fields keeps the user Bare.</summary>
    [HumansFact(Timeout = 10000)]
    public async Task ProfileEditorService_SaveProfileAsync_StaysBare_WhenRequiredFieldsBlank()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var request = MakeRequest(burnerName: "", firstName: "", lastName: "");

        await _editor.SaveProfileAsync(userId, "Stub", request, ct: Xunit.TestContext.Current.CancellationToken);

        var user = await Db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, Xunit.TestContext.Current.CancellationToken);
        user.State.Should().Be(UserState.Bare);
    }

    [HumansFact]
    public async Task SaveProfileAsync_ExistingProfile_UpdatesFields()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        var request = MakeRequest(burnerName: "NewName", firstName: "Updated", lastName: "Person");

        await _editor.SaveProfileAsync(userId, "Updated Person", request, ct: Xunit.TestContext.Current.CancellationToken);

        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId, Xunit.TestContext.Current.CancellationToken);
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

        await _editor.SaveProfileAsync(userId, "New Display Name", request, ct: Xunit.TestContext.Current.CancellationToken);

        var user = await Db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, Xunit.TestContext.Current.CancellationToken);
        user.DisplayName.Should().Be("New Display Name");
    }

    [HumansFact]
    public async Task SaveProfileAsync_ParsesBirthday_ValidDate()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        var request = MakeRequest(birthdayMonth: 2, birthdayDay: 14);

        await _editor.SaveProfileAsync(userId, "Test", request, ct: Xunit.TestContext.Current.CancellationToken);

        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId, Xunit.TestContext.Current.CancellationToken);
        profile.DateOfBirth.Should().Be(new LocalDate(4, 2, 14));
    }

    [HumansFact]
    public async Task SaveProfileAsync_ParsesBirthday_InvalidDay_SetsNull()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        var request = MakeRequest(birthdayMonth: 2, birthdayDay: 30);

        await _editor.SaveProfileAsync(userId, "Test", request, ct: Xunit.TestContext.Current.CancellationToken);

        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId, Xunit.TestContext.Current.CancellationToken);
        profile.DateOfBirth.Should().BeNull();
    }

    // --- Profile picture write paths (file share is the source of truth; the
    // DB bytes column is obsolete and untouched by code) ---

    [HumansFact]
    public async Task SaveProfileAsync_UploadsProfilePicture_WritesToFilesystem()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, withPicture: true);
        var payload = new byte[] { 0x10, 0x20, 0x30 };
        var request = MakeRequest(pictureData: payload, pictureContentType: "image/jpeg");

        await _editor.SaveProfileAsync(userId, "Test", request, ct: Xunit.TestContext.Current.CancellationToken);

        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId, Xunit.TestContext.Current.CancellationToken);
        // Content-type column is the "has picture" marker + extension source.
        profile.ProfilePictureContentType.Should().Be("image/jpeg");
        // Bytes live on the file share at the versioned key stored on the profile.
        var key = profile.ProfilePictureStorageKey!;
        key.Should().StartWith($"uploads/profile-pictures/{profile.Id}-").And.EndWith(".jpg");
        _fileStorage.Files.Should().ContainKey(key);
        _fileStorage.Files[key].Should().BeEquivalentTo(payload);
        _fileStorage.Files.Should().NotContainKey(PicKey(profile.Id, "image/png"));
    }

    [HumansFact]
    public async Task SaveProfileAsync_FailedPictureReplacementPreservesThePreviousPicture()
    {
        var userId = Guid.NewGuid();
        var profileId = await SeedUserWithProfileAsync(userId, withPicture: true);
        _pictureStorage.SaveAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("Disk full"));

        await _editor.SaveProfileAsync(userId, "Updated name",
            MakeRequest(pictureData: [4, 5, 6], pictureContentType: "image/jpeg"), ct: Xunit.TestContext.Current.CancellationToken);

        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId, Xunit.TestContext.Current.CancellationToken);
        profile.ProfilePictureContentType.Should().Be("image/png");
        (await Db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, Xunit.TestContext.Current.CancellationToken))
            .DisplayName.Should().Be("Updated name");
        _fileStorage.Files[PicKey(profileId, "image/png")].Should().Equal(1, 2, 3);
        var picture = await _service.GetProfilePictureAsync(profileId, Xunit.TestContext.Current.CancellationToken);
        picture.Should().NotBeNull();
        picture!.Value.ContentType.Should().Be("image/png");
        picture.Value.Data.Should().Equal(1, 2, 3);
    }

    [HumansFact]
    public async Task SaveProfileAsync_FailedFirstPictureUploadDoesNotPublishMissingFileMetadata()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        _pictureStorage.SaveAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("Disk full"));

        await _editor.SaveProfileAsync(userId, "Updated name",
            MakeRequest(pictureData: [4, 5, 6], pictureContentType: "image/jpeg"), ct: Xunit.TestContext.Current.CancellationToken);

        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId, Xunit.TestContext.Current.CancellationToken);
        profile.ProfilePictureContentType.Should().BeNull();
        (await _service.GetProfilePictureAsync(profile.Id, Xunit.TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [HumansFact]
    public async Task SaveProfileAsync_CancelledPictureWritePreservesThePreviousPicture()
    {
        var userId = Guid.NewGuid();
        var profileId = await SeedUserWithProfileAsync(userId, withPicture: true);
        _pictureStorage.SaveAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var act = () => _editor.SaveProfileAsync(userId, "Updated name",
            MakeRequest(pictureData: [4, 5, 6], pictureContentType: "image/jpeg"), ct: Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId, Xunit.TestContext.Current.CancellationToken);
        profile.ProfilePictureContentType.Should().Be("image/png");
        _fileStorage.Files[PicKey(profileId, "image/png")].Should().Equal(1, 2, 3);
    }

    [HumansFact]
    public async Task SaveProfileAsync_OldFileCleanupFailurePreservesTheNewPicture()
    {
        var userId = Guid.NewGuid();
        var profileId = await SeedUserWithProfileAsync(userId, withPicture: true);
        _pictureStorage.DeleteAsync(PicKey(profileId, "image/png"), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("File locked"));

        await _editor.SaveProfileAsync(userId, "Updated name",
            MakeRequest(pictureData: [4, 5, 6], pictureContentType: "image/jpeg"), ct: Xunit.TestContext.Current.CancellationToken);

        var picture = await _service.GetProfilePictureAsync(profileId, Xunit.TestContext.Current.CancellationToken);
        picture.Should().NotBeNull();
        picture!.Value.ContentType.Should().Be("image/jpeg");
        picture.Value.Data.Should().Equal(4, 5, 6);
        await _pictureStorage.Received(1).DeleteAsync(PicKey(profileId, "image/png"), CancellationToken.None);
    }

    [HumansFact]
    public async Task SaveProfileAsync_SameContentTypeReplace_FailedMetadataSaveKeepsThePreviousBytes()
    {
        var userId = Guid.NewGuid();
        var profileId = await SeedUserWithProfileAsync(userId, withPicture: true);
        var existing = await Db.Profiles.AsNoTracking().FirstAsync(p => p.Id == profileId, Xunit.TestContext.Current.CancellationToken);
        var failingRepo = Substitute.For<IUserRepository>();
        failingRepo.GetByUserIdAsync(userId, Arg.Any<CancellationToken>()).Returns(existing);
        failingRepo.UpdateAsync(Arg.Any<Profile>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<CVEntry>?>(), Arg.Any<string?>())
            .ThrowsAsync(new InvalidOperationException("Database unavailable"));
        var sut = new UserService(
            failingRepo,
            _communicationPreferenceRepository,
            AdminAuthorization,
            Substitute.For<IRoleAssignmentClaimsCacheInvalidator>(),
            _pictureStorage,
            Clock,
            NullLogger<UserService>.Instance, AuditLog);
        var command = new UserProfileSaveCommand(
            "Updated name", "TestBurner", "Test", "User", null, null, null, null, null, null, null, null, null,
            null, null, null, null, null, false, UserProfilePictureMutation.Set, "image/png");

        var act = () => sut.SaveProfileAsync(userId, command, [4, 5, 6], Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _fileStorage.Files[PicKey(profileId, "image/png")].Should().Equal(1, 2, 3);
        _fileStorage.Files.Keys.Should().ContainSingle().Which.Should().Be(PicKey(profileId, "image/png"));
        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.Id == profileId, Xunit.TestContext.Current.CancellationToken);
        profile.ProfilePictureContentType.Should().Be("image/png");
        profile.ProfilePictureStorageKey.Should().BeNull();
    }

    [HumansFact]
    public async Task SaveProfileAsync_LegacyNameSaveFailure_PreservesProfileAndPreviousPicture()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = Guid.NewGuid();
        var profileId = await SeedUserWithProfileAsync(userId, withPicture: true);
        var options = new DbContextOptionsBuilder<UsersDbContext>(DbOptions)
            .AddInterceptors(new RejectLegacyNameSave()).Options;
        var sut = new UserService(
            new UserRepository(new TestDbContextFactory(options), Clock),
            _communicationPreferenceRepository, AdminAuthorization,
            Substitute.For<IRoleAssignmentClaimsCacheInvalidator>(),
            _pictureStorage, Clock, NullLogger<UserService>.Instance, AuditLog);
        var command = new UserProfileSaveCommand(
            "Updated name", "TestBurner", "Test", "User", null, null, null, null, null, null, null, null, null,
            null, null, null, null, null, false, UserProfilePictureMutation.Set, "image/png");

        var act = () => sut.SaveProfileAsync(userId, command, [4, 5, 6], ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Legacy name save failed");
        var saved = await Db.Profiles.AsNoTracking().SingleAsync(p => p.Id == profileId, ct);
        saved.BurnerName.Should().Be("OldName");
        saved.ProfilePictureStorageKey.Should().BeNull();
        _fileStorage.Files.Keys.Should().ContainSingle().Which.Should().Be(PicKey(profileId, "image/png"));
        _fileStorage.Files[PicKey(profileId, "image/png")].Should().Equal(1, 2, 3);
        (await Db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct))
            .DisplayName.Should().Be("Test User");
    }

    private sealed class RejectLegacyNameSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<User>()
                .Any(entry => entry.Property(user => user.DisplayName).IsModified))
                throw new InvalidOperationException("Legacy name save failed");
            return ValueTask.FromResult(result);
        }
    }

    [HumansFact]
    public async Task SaveProfileAsync_SuccessfulReplace_WritesAVersionedKeyAndDeletesThePreviousFile()
    {
        var userId = Guid.NewGuid();
        var profileId = await SeedUserWithProfileAsync(userId, withPicture: true);

        await _editor.SaveProfileAsync(userId, "Updated name",
            MakeRequest(pictureData: [4, 5, 6], pictureContentType: "image/png"), ct: Xunit.TestContext.Current.CancellationToken);

        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.Id == profileId, Xunit.TestContext.Current.CancellationToken);
        profile.ProfilePictureStorageKey.Should().NotBeNullOrEmpty()
            .And.NotBe(PicKey(profileId, "image/png"));
        _fileStorage.Files.Should().NotContainKey(PicKey(profileId, "image/png"));
        _fileStorage.Files.Keys.Should().ContainSingle().Which.Should().Be(profile.ProfilePictureStorageKey);
        var picture = await _service.GetProfilePictureAsync(profileId, Xunit.TestContext.Current.CancellationToken);
        picture!.Value.Data.Should().Equal(4, 5, 6);

        var firstVersionedKey = profile.ProfilePictureStorageKey!;
        await _editor.SaveProfileAsync(userId, "Updated name",
            MakeRequest(pictureData: [7, 8, 9], pictureContentType: "image/png"), ct: Xunit.TestContext.Current.CancellationToken);

        var replaced = await Db.Profiles.AsNoTracking().FirstAsync(p => p.Id == profileId, Xunit.TestContext.Current.CancellationToken);
        replaced.ProfilePictureStorageKey.Should().NotBe(firstVersionedKey);
        _fileStorage.Files.Keys.Should().ContainSingle().Which.Should().Be(replaced.ProfilePictureStorageKey);
    }

    [HumansFact]
    public async Task GetProfilePictureAsync_NullStorageKey_ResolvesTheLegacyDeterministicKey()
    {
        var userId = Guid.NewGuid();
        var profileId = await SeedUserWithProfileAsync(userId, withPicture: true);
        (await Db.Profiles.AsNoTracking().FirstAsync(p => p.Id == profileId, Xunit.TestContext.Current.CancellationToken))
            .ProfilePictureStorageKey.Should().BeNull();

        var picture = await _service.GetProfilePictureAsync(profileId, Xunit.TestContext.Current.CancellationToken);

        picture.Should().NotBeNull();
        picture!.Value.Data.Should().Equal(1, 2, 3);
    }

    [HumansFact]
    public async Task EraseForUserAsync_DeletesTheVersionedPictureFile()
    {
        var userId = Guid.NewGuid();
        var profileId = await SeedUserWithProfileAsync(userId, withPicture: true);
        const string versionedKey = "uploads/profile-pictures/versioned.png";
        var profile = await Db.Profiles.FirstAsync(p => p.Id == profileId, Xunit.TestContext.Current.CancellationToken);
        profile.ProfilePictureStorageKey = versionedKey;
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        await _fileStorage.SaveAsync(versionedKey, [4, 5, 6], Xunit.TestContext.Current.CancellationToken);

        await _storageUserService.EraseForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        _fileStorage.Files.Should().NotContainKey(versionedKey);
    }

    [HumansFact]
    public async Task SaveProfileAsync_RemoveProfilePicture_ClearsContentTypeAndDeletesFile()
    {
        var userId = Guid.NewGuid();
        var profileId = await SeedUserWithProfileAsync(userId, withPicture: true);

        var request = MakeRequest(removeProfilePicture: true);
        await _editor.SaveProfileAsync(userId, "Test", request, ct: Xunit.TestContext.Current.CancellationToken);

        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId, Xunit.TestContext.Current.CancellationToken);
        profile.ProfilePictureContentType.Should().BeNull();
        _fileStorage.Files.Should().NotContainKey(PicKey(profileId, "image/png"));
    }


    [HumansFact]
    public async Task GetProfilePictureAsync_WithPicture_ReturnsData()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, withPicture: true);
        var profile = await Db.Profiles.FirstAsync(p => p.UserId == userId, Xunit.TestContext.Current.CancellationToken);

        var result = await _service.GetProfilePictureAsync(profile.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.Value.Data.Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
        result.Value.ContentType.Should().Be("image/png");
    }

    [HumansFact]
    public async Task GetProfilePictureAsync_NoPicture_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, withPicture: false);
        var profile = await Db.Profiles.FirstAsync(p => p.UserId == userId, Xunit.TestContext.Current.CancellationToken);

        var result = await _service.GetProfilePictureAsync(profile.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetProfilePictureAsync_NoProfile_ReturnsNull()
    {
        var result = await _service.GetProfilePictureAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

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
        var profile = await Db.Profiles.FirstAsync(p => p.UserId == userId, Xunit.TestContext.Current.CancellationToken);

        var fsPayload = new byte[] { 9, 9, 9, 9 };
        await _fileStorage.SaveAsync(PicKey(profile.Id, "image/png"), fsPayload, Xunit.TestContext.Current.CancellationToken);

        var result = await _service.GetProfilePictureAsync(profile.Id, Xunit.TestContext.Current.CancellationToken);

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
        var profile = await Db.Profiles.FirstAsync(p => p.UserId == userId, Xunit.TestContext.Current.CancellationToken);

        await _fileStorage.DeleteAsync(PicKey(profile.Id, "image/png"), Xunit.TestContext.Current.CancellationToken);

        var result = await _service.GetProfilePictureAsync(profile.Id, Xunit.TestContext.Current.CancellationToken);

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
        var profile = await Db.Profiles.FirstAsync(p => p.UserId == userId, Xunit.TestContext.Current.CancellationToken);

        // Clear the gate as if anonymization had run.
        var tracked = await Db.Profiles.FirstAsync(p => p.UserId == userId, Xunit.TestContext.Current.CancellationToken);
        tracked.ProfilePictureContentType = null;
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Confirm the stale file is still on disk to make the gate meaningful.
        _fileStorage.Files.ContainsKey(PicKey(profile.Id, "image/png")).Should().BeTrue();

        var result = await _service.GetProfilePictureAsync(profile.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeNull("DB content-type is null after anonymization, so the stale on-disk file must not be served");
    }

    // Profile-index/edit/admin-detail bundling is a controller concern:
    // Profile + Application data are fetched separately and assembled for the view.

    // Birthday/Location widgets read directly from the UserInfo cache via
    // CachingUserService.

    // --- Cooldown and export ---

    // The search surface lives on IUserManagement.SearchUsersAsync and is
    // covered by CachingUserServiceTests.

    [HumansFact]
    public async Task SaveProfileLanguages_OwnerLookupFailure_DoesNotCommitWithoutCacheRefresh()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var profileId = await SeedUserWithProfileAsync(Guid.NewGuid());
        Db.ProfileLanguages.Add(new ProfileLanguage
        {
            Id = Guid.NewGuid(), ProfileId = profileId,
            LanguageCode = "es", Proficiency = LanguageProficiency.Native,
        });
        await Db.SaveChangesAsync(ct);
        var repository = Substitute.For<IUserRepository>();
        repository.ReplaceLanguagesAsync(profileId, Arg.Any<IReadOnlyList<ProfileLanguage>>(), Arg.Any<CancellationToken>())
            .Returns(c => _userRepository.ReplaceLanguagesAsync(profileId,
                c.Arg<IReadOnlyList<ProfileLanguage>>(), c.Arg<CancellationToken>()));
        repository.GetOwnerUserIdAsync(profileId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Owner lookup failed"));
        var service = new UserService(repository, _communicationPreferenceRepository, AdminAuthorization,
            Substitute.For<IRoleAssignmentClaimsCacheInvalidator>(), _pictureStorage,
            Clock, NullLogger<UserService>.Instance, AuditLog);

        var act = () => service.SaveProfileLanguagesAsync(profileId,
            [new ProfileLanguageInfo(Guid.Empty, "fr", LanguageProficiency.Native)], ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Owner lookup failed");
        (await Db.ProfileLanguages.AsNoTracking().SingleAsync(ct)).LanguageCode.Should().Be("es");
    }

    /// <summary>
    /// Builds a <see cref="UserService"/> with a custom <see cref="IUserRepository"/>
    /// while keeping all other dependencies wired to the same test-class fields.
    /// </summary>
    private UserService BuildUserServiceWith(IUserRepository userRepository) => new(
        userRepository,
        _communicationPreferenceRepository,
        AdminAuthorization,
        Substitute.For<IRoleAssignmentClaimsCacheInvalidator>(),
        Substitute.For<IFileStorage>(),
        Clock,
        NullLogger<UserService>.Instance, AuditLog);

    // --- Helpers ---

    // --- SaveProfileAsync workflow (audit P1-P4) ---

    [HumansFact]
    public async Task SaveProfileAsync_AllergyOtherWithoutText_ReturnsRefusalWithoutWrites()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var request = MakeRequest() with
        {
            Allergies = [DietaryOptions.OtherOption],
            AllergyOtherText = "   ",
        };

        var act = () => _editor.SaveProfileAsync(userId, "Test", request, ct: Xunit.TestContext.Current.CancellationToken);

        var result = await act();

        result.Succeeded.Should().BeFalse();
        result.ProfileId.Should().BeNull();
        result.ErrorKey.Should().Be("Profile_DietaryMedical_AllergyOther_Required");
        await _userService.DidNotReceiveWithAnyArgs().SaveProfileAsync(default, default!, default, default);
        (await Db.Profiles.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [HumansFact]
    public async Task SaveProfileAsync_EmptyCvWithoutNoPriorExperienceFlag_ReturnsRefusalWithoutWrites()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var request = MakeRequest() with { VolunteerHistory = [], NoPriorBurnExperience = false };

        var act = () => _editor.SaveProfileAsync(userId, "Test", request, ct: Xunit.TestContext.Current.CancellationToken);

        var result = await act();

        result.Succeeded.Should().BeFalse();
        result.ProfileId.Should().BeNull();
        result.ErrorKey.Should().Be("Profile_BurnerCVRequired");
        await _userService.DidNotReceiveWithAnyArgs().SaveProfileAsync(default, default!, default, default);
        (await Db.Profiles.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [HumansFact]
    public async Task SaveProfileAsync_NullCv_SkipsCompletenessCheck_AndLeavesHistoryUntouched()
    {
        // Name-only saves (onboarding widget) pass no CV payload — they must not
        // be blocked by the completeness rule nor overwrite stored history.
        var userId = Guid.NewGuid();
        var profileId = await SeedUserWithProfileAsync(userId);
        Db.VolunteerHistoryEntries.Add(new VolunteerHistoryEntry
        {
            Id = Guid.NewGuid(), ProfileId = profileId,
            Date = new LocalDate(2025, 7, 1), EventName = "Existing history",
            CreatedAt = Clock.GetCurrentInstant(), UpdatedAt = Clock.GetCurrentInstant(),
        });
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        var request = MakeRequest() with { VolunteerHistory = null, NoPriorBurnExperience = false };

        await _editor.SaveProfileAsync(userId, "Test", request, ct: Xunit.TestContext.Current.CancellationToken);

        await _userService.Received(1).SaveProfileAsync(userId, Arg.Any<UserProfileSaveCommand>(),
            Arg.Any<byte[]?>(), Arg.Any<CancellationToken>(), null);
        (await Db.VolunteerHistoryEntries.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken))
            .EventName.Should().Be("Existing history");
    }

    [HumansFact]
    public async Task SaveProfileAsync_NameOnlySavePreservesIncompleteDietaryData()
    {
        var userId = Guid.NewGuid();
        var profileId = await SeedUserWithProfileAsync(userId);
        var existing = await Db.Profiles.FirstAsync(profile => profile.Id == profileId, Xunit.TestContext.Current.CancellationToken);
        existing.DietaryPreference = "Vegan";
        existing.Allergies = [DietaryOptions.OtherOption];
        existing.AllergyOtherText = null;
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _editor.SaveProfileAsync(userId, "Named", MakeRequest(burnerName: "Named"), Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        var saved = await Db.Profiles.AsNoTracking().FirstAsync(profile => profile.Id == profileId, Xunit.TestContext.Current.CancellationToken);
        saved.BurnerName.Should().Be("Named");
        saved.DietaryPreference.Should().Be("Vegan");
        saved.Allergies.Should().Equal(DietaryOptions.OtherOption);
        saved.AllergyOtherText.Should().BeNull();
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task SaveProfileAsync_CvSaveFailure_PreservesOriginalDeclarationAndHistory(bool hadHistory)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = Guid.NewGuid();
        var profileId = await SeedUserWithProfileAsync(userId);
        var original = await Db.Profiles.SingleAsync(p => p.Id == profileId, ct);
        original.NoPriorBurnExperience = !hadHistory;
        if (hadHistory)
            Db.VolunteerHistoryEntries.Add(new VolunteerHistoryEntry
            {
                Id = Guid.NewGuid(), ProfileId = profileId,
                Date = new LocalDate(2025, 7, 1), EventName = "Original history",
                CreatedAt = Clock.GetCurrentInstant(), UpdatedAt = Clock.GetCurrentInstant(),
            });
        await Db.SaveChangesAsync(ct);
        var options = new DbContextOptionsBuilder<UsersDbContext>(DbOptions)
            .AddInterceptors(new RejectCvSave()).Options;
        var failingService = BuildUserServiceWith(new UserRepository(new TestDbContextFactory(options), Clock));
        _userService.SaveProfileAsync(userId, Arg.Any<UserProfileSaveCommand>(), Arg.Any<byte[]?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<CVEntry>?>())
            .Returns(call => failingService.SaveProfileAsync(userId,
                call.Arg<UserProfileSaveCommand>(), call.Arg<byte[]?>(), call.Arg<CancellationToken>(), call.Arg<IReadOnlyList<CVEntry>?>()));
        var request = MakeRequest() with
        {
            NoPriorBurnExperience = hadHistory,
            VolunteerHistory = hadHistory ? [] : [new CVEntry(Guid.Empty, new LocalDate(2025, 7, 1), "New history", null)],
        };

        var act = () => _editor.SaveProfileAsync(userId, "Test", request, ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("CV save failed");
        (await Db.Profiles.AsNoTracking().SingleAsync(p => p.Id == profileId, ct))
            .NoPriorBurnExperience.Should().Be(!hadHistory);
        var savedHistory = await Db.VolunteerHistoryEntries.AsNoTracking().Where(h => h.ProfileId == profileId).ToListAsync(ct);
        if (hadHistory)
            savedHistory.Should().ContainSingle().Which.EventName.Should().Be("Original history");
        else
            savedHistory.Should().BeEmpty();
    }

    private sealed class RejectCvSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<VolunteerHistoryEntry>()
                .Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
                throw new InvalidOperationException("CV save failed");
            return ValueTask.FromResult(result);
        }
    }

    [HumansFact(Timeout = 10000)]
    public async Task SaveProfileAsync_OverlappingSubmissions_PreserveEachProfilesCvDeclaration()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = Guid.NewGuid();
        var profileId = await SeedUserWithProfileAsync(userId);
        var historyReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeHistory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _userService.SaveProfileAsync(userId, Arg.Any<UserProfileSaveCommand>(), Arg.Any<byte[]?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<CVEntry>?>())
            .Returns(async call =>
            {
                var entries = call.Arg<IReadOnlyList<CVEntry>?>();
                if (entries is { Count: 0 })
                {
                    historyReached.TrySetResult();
                    await resumeHistory.Task.WaitAsync(ct);
                }
                return await _storageUserService.SaveProfileAsync(userId, call.Arg<UserProfileSaveCommand>(),
                    call.Arg<byte[]?>(), call.Arg<CancellationToken>(), entries);
            });
        var first = _editor.SaveProfileAsync(userId, "Test", MakeRequest() with
        {
            NoPriorBurnExperience = true, VolunteerHistory = [],
        }, ct);
        try
        {
            await historyReached.Task.WaitAsync(ct);
            var secondEditor = new ProfileEditorService(_userService, NullLogger<ProfileEditorService>.Instance);
            var second = secondEditor.SaveProfileAsync(userId, "Test", MakeRequest() with
            {
                NoPriorBurnExperience = false,
                VolunteerHistory = [new CVEntry(Guid.Empty, new LocalDate(2025, 7, 1), "Nowhere", null)],
            }, ct);
            await Task.WhenAny(second, Task.Delay(100, ct));
            resumeHistory.TrySetResult();
            var results = await Task.WhenAll(first, second);
            results.Should().AllSatisfy(result => result.Succeeded.Should().BeTrue());
        }
        finally
        {
            resumeHistory.TrySetResult();
        }

        (await Db.Profiles.AsNoTracking().SingleAsync(p => p.Id == profileId, ct))
            .NoPriorBurnExperience.Should().BeFalse();
        (await Db.VolunteerHistoryEntries.AsNoTracking().Where(h => h.ProfileId == profileId).ToListAsync(ct))
            .Should().ContainSingle().Which.EventName.Should().Be("Nowhere");
    }

    [HumansFact]
    public async Task SaveProfileAsync_WithCvPayload_SavesVolunteerHistory()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);
        var entry = new CVEntry(Guid.Empty, new LocalDate(2024, 7, 1), "Nowhere", null);
        var request = MakeRequest() with { VolunteerHistory = [entry] };

        await _editor.SaveProfileAsync(userId, "Test", request, ct: Xunit.TestContext.Current.CancellationToken);

        await _userService.Received(1).SaveProfileAsync(userId, Arg.Any<UserProfileSaveCommand>(),
            Arg.Any<byte[]?>(), Arg.Any<CancellationToken>(),
            Arg.Is<IReadOnlyList<CVEntry>>(l => l.Count == 1 && l[0].EventName == "Nowhere"));
        (await Db.VolunteerHistoryEntries.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken))
            .EventName.Should().Be("Nowhere");
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task SaveDietaryMedicalAsync_returns_other_description_refusals_without_writes(bool intolerance)
    {
        var command = new UserProfileDietaryMedicalCommand(null,
            intolerance ? [] : [DietaryOptions.OtherOption], " ",
            intolerance ? [DietaryOptions.OtherOption] : [], " ", "Existing medical details");

        var result = await _editor.SaveDietaryMedicalAsync(Guid.NewGuid(), command, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorKey.Should().Be(intolerance
            ? "Profile_DietaryMedical_IntoleranceOther_Required" : "Profile_DietaryMedical_AllergyOther_Required");
        await _userService.DidNotReceiveWithAnyArgs().SaveDietaryMedicalAsync(default, default!, default);
    }

    private async Task SeedUserAsync(Guid userId,
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
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
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
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        if (withPicture)
        {
            await _fileStorage.SaveAsync(PicKey(profile.Id, "image/png"), [1, 2, 3], Xunit.TestContext.Current.CancellationToken);
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

}

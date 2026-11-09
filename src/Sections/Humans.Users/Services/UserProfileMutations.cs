using Humans.AuditLog.Contracts;
using Humans.Base.Helpers;
using Humans.Base.Interfaces;
using Humans.Base.Threading;
using Humans.Users.Contracts;
using Humans.Users.Data.Repositories;
using NodaTime;

namespace Humans.Users.Services;

internal sealed class UserProfileMutations(
    IUserRepository repo,
    IFileStorage fileStorage,
    IClock clock,
    IAuditLogService audit,
    ILogger<UserService> logger) : IApplicationService
{
    private readonly UserInfoReader infoReader = new(repo);

    private static readonly TrackedLock[] ProfileStubLocks = Enumerable
        .Range(0, 32)
        .Select(i => new TrackedLock($"UserService.ProfileStub[{i}]"))
        .ToArray();

    private static TrackedLock ProfileStubLockFor(Guid userId) =>
        ProfileStubLocks[(uint)userId.GetHashCode() % (uint)ProfileStubLocks.Length];

    public async Task<bool> EnsureStubProfileAsync(
        Guid userId,
        string? burnerName = null,
        string? firstName = null,
        string? lastName = null,
        CancellationToken ct = default,
        Guid? actorUserId = null)
    {
        using var _ = await ProfileStubLockFor(userId).AcquireAsync(logger, ct);

        var existing = await repo.GetByUserIdAsync(userId, ct);
        if (existing is not null) return false;

        var info = await infoReader.GetUserInfoAsync(userId, ct);
        if (info is null)
            return false;

        if (info.IsTombstone)
        {
            logger.LogError(
                "EnsureStubProfileAsync called for tombstone user {UserId} (MergedAt={MergedAt}) - refusing to create Stub Profile",
                userId, info.MergedAt);
            return false;
        }

        // OAuth/import creation seeds User.BurnerName before any Profile exists; inherit it, or
        // the Profile->User mirror below would wipe it. User legal names can't be set yet.
        var user = await repo.GetByIdAsync(userId, ct);
        var resolvedBurnerName = burnerName ?? user?.BurnerName;

        var now = clock.GetCurrentInstant();
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
            BurnerName = (resolvedBurnerName ?? string.Empty).Trim(),
            FirstName = (firstName ?? string.Empty).Trim(),
            LastName = (lastName ?? string.Empty).Trim(),
        };

        // Seeded names (magic-link signup) promote straight to Active; import/OAuth stubs carry no
        // legal names and stay Bare. The repository derives User.State.
        await repo.AddAsync(profile, ct);
        if (actorUserId.HasValue)
            await audit.LogAsync(UsersAuditActions.StubProfileBackfilled, nameof(User), userId,
                "Created missing profile.", actorUserId.Value);
        return true;
    }

    public async Task<UserProfileSaveResult> SaveProfileAsync(
        Guid userId,
        UserProfileSaveCommand command,
        byte[]? profilePictureData = null,
        CancellationToken ct = default,
        IReadOnlyList<CVEntry>? volunteerHistory = null)
    {
        using var _ = await ProfileStubLockFor(userId).AcquireAsync(logger, ct);

        var now = clock.GetCurrentInstant();
        var profile = await repo.GetByUserIdAsync(userId, ct);

        if (profile is null)
        {
            profile = new Profile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await repo.AddAsync(profile, ct);
        }

        var previousPictureKey = ProfilePictureKeys.Resolve(
            profile.Id, profile.ProfilePictureContentType, profile.ProfilePictureStorageKey);

        profile.BurnerName = command.BurnerName;
        profile.FirstName = command.FirstName;
        profile.LastName = command.LastName;
        profile.City = command.City;
        profile.CountryCode = command.CountryCode;
        profile.Latitude = command.Latitude;
        profile.Longitude = command.Longitude;
        profile.PlaceId = command.PlaceId;
        profile.Bio = command.Bio?.TrimEnd();
        profile.Pronouns = command.Pronouns;
        profile.ContributionInterests = command.ContributionInterests?.TrimEnd();
        profile.BoardNotes = command.BoardNotes?.TrimEnd();
        profile.EmergencyContactName = command.EmergencyContactName;
        profile.EmergencyContactPhone = command.EmergencyContactPhone;
        profile.EmergencyContactRelationship = command.EmergencyContactRelationship;
        profile.NoPriorBurnExperience = command.NoPriorBurnExperience;

        profile.UpdatedAt = now;

        // Year 4 is a leap year, so Feb 29 validates.
        if (command.BirthdayMonth is >= 1 and <= 12 && command.BirthdayDay is >= 1 and <= 31)
        {
            try
            {
                profile.DateOfBirth = new LocalDate(4, command.BirthdayMonth.Value, command.BirthdayDay.Value);
            }
            catch (ArgumentOutOfRangeException)
            {
                profile.DateOfBirth = null;
            }
        }
        else
        {
            profile.DateOfBirth = null;
        }

        // Every upload lands on a fresh key, so the previous bytes survive until the editor deletes
        // them after this save commits.
        var pictureMutation = command.PictureMutation;
        string? newPictureKey = null;
        if (pictureMutation == UserProfilePictureMutation.Set
            && profilePictureData is not null && command.ProfilePictureContentType is not null)
        {
            try
            {
                newPictureKey = ProfilePictureKeys.NewVersioned(profile.Id, command.ProfilePictureContentType);
                await fileStorage.SaveAsync(newPictureKey, profilePictureData, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Failed to write profile picture for {ProfileId}; preserving the previous picture metadata",
                    profile.Id);
                newPictureKey = null;
                pictureMutation = UserProfilePictureMutation.None;
            }
        }

        switch (pictureMutation)
        {
            case UserProfilePictureMutation.Remove:
                profile.ProfilePictureContentType = null;
                profile.ProfilePictureStorageKey = null;
                break;
            case UserProfilePictureMutation.Set:
                profile.ProfilePictureContentType = command.ProfilePictureContentType;
                profile.ProfilePictureStorageKey = newPictureKey;
                break;
        }

        var currentPictureKey = ProfilePictureKeys.Resolve(
            profile.Id, profile.ProfilePictureContentType, profile.ProfilePictureStorageKey);
        try
        {
            await repo.UpdateAsync(profile, ct, volunteerHistory);
            return new UserProfileSaveResult(profile.Id, previousPictureKey, currentPictureKey);
        }
        finally
        {
            if (newPictureKey is not null || !string.Equals(previousPictureKey, currentPictureKey, StringComparison.Ordinal))
                await DeleteSupersededPictureAsync(profile.Id, previousPictureKey, newPictureKey);
        }
    }

    internal async Task DeleteSupersededPictureAsync(Guid profileId, params string?[] keys)
    {
        var candidates = keys.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (candidates.Count == 0) return;
        try
        {
            var picture = await repo.GetProfilePictureLocationAsync(profileId, CancellationToken.None);
            var currentKey = picture is { } current
                ? ProfilePictureKeys.Resolve(profileId, current.ContentType, current.StorageKey)
                : null;
            foreach (var key in candidates.Where(key => !string.Equals(key, currentKey, StringComparison.Ordinal)))
            {
                try
                {
                    await fileStorage.DeleteAsync(key, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete superseded profile picture for {ProfileId}", profileId);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to verify profile picture references for {ProfileId}; retaining files", profileId);
        }
    }

    public async Task SaveMedicalConditionsAsync(
        Guid userId,
        string? medicalConditions,
        CancellationToken ct = default)
    {
        using var _ = await ProfileStubLockFor(userId).AcquireAsync(logger, ct);

        var now = clock.GetCurrentInstant();
        var profile = await repo.GetByUserIdAsync(userId, ct);
        if (profile is null)
        {
            profile = new Profile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await repo.AddAsync(profile, ct);
        }

        profile.MedicalConditions = medicalConditions;
        profile.UpdatedAt = now;

        await repo.UpdateAsync(profile, ct);
    }

    public async Task<UserProfileLanguagesSaveResult> SaveProfileLanguagesAsync(
        Guid profileId,
        IReadOnlyList<ProfileLanguageInfo> languages,
        CancellationToken ct = default)
    {
        var rows = languages
            .Select(l => new ProfileLanguage
            {
                Id = l.Id == Guid.Empty ? Guid.NewGuid() : l.Id,
                ProfileId = profileId,
                LanguageCode = l.LanguageCode,
                Proficiency = l.Proficiency,
            })
            .ToList();

        var ownerUserId = await repo.GetOwnerUserIdAsync(profileId, ct);
        await repo.ReplaceLanguagesAsync(profileId, rows, ct);
        return new UserProfileLanguagesSaveResult(
            ownerUserId is not null,
            ownerUserId);
    }
}

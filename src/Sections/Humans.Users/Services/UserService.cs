using Humans.Auth.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Onboarding.Contracts;
using Humans.Users.Data.Repositories;
using Humans.Users.Contracts;
using Humans.Base.Helpers;
using Humans.Users.Helpers;
using Humans.Gdpr.Contracts;
using NodaTime;

namespace Humans.Users.Services;

internal sealed class UserService(
    IUserRepository repo,
    IAdminAuthorizationService adminAuthorization,
    IFileStorage fileStorage,
    IClock clock,
    ILogger<UserService> logger,
    IAuditLogService audit) : IUserManagement, IUserDataContributor
{
    internal const string Account = UserDataExport.Account;
    internal const string Profile = UserDataExport.Profile;
    internal const string MergedProfiles = UserDataExport.MergedProfiles;
    internal const string ContactFields = UserDataExport.ContactFields;
    internal const string UserEmails = UserDataExport.UserEmails;
    internal const string VolunteerHistory = UserDataExport.VolunteerHistory;
    internal const string Languages = UserDataExport.Languages;

    private readonly UserInfoReader infoReader = new(repo);
    private readonly UserDataExport dataExport = new(repo);
    private readonly UserProfileMutations profileMutations = new(repo, fileStorage, clock, audit, logger);

    private readonly UserEmailMutations emailMutations = new(repo, clock, logger);

    /// <summary>
    /// Only <c>CachingUserService</c> sees the whole graph, so here raw and redirecting reads are
    /// the same; the redirect is tested through the decorator.
    /// </summary>
    public ValueTask<UserInfo?> GetRawUserInfoAsync(Guid userId, CancellationToken ct = default) =>
        GetUserInfoAsync(userId, ct);

    /// <inheritdoc cref="GetRawUserInfoAsync" />
    public Task<IReadOnlyCollection<UserInfo>> GetAllRawUserInfosAsync(CancellationToken ct = default) =>
        GetAllUserInfosAsync(ct);

    public ValueTask<UserInfo?> GetUserInfoAsync(Guid userId, CancellationToken ct = default) =>
        infoReader.GetUserInfoAsync(userId, ct);

    public Task<IReadOnlyCollection<UserInfo>> GetAllUserInfosAsync(CancellationToken ct = default) =>
        infoReader.GetAllUserInfosAsync(ct);

    public ValueTask<IReadOnlyDictionary<Guid, UserInfo>> GetUserInfosAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        infoReader.GetUserInfosAsync(userIds, ct);

    public Task<IReadOnlyList<HumanSearchResult>> SearchUsersAsync(
        string query, PersonSearchFields fields, int limit = 10, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "SearchUsersAsync runs against the cached UserInfo snapshot in " +
            "CachingUserService. If this is being called on the inner UserService " +
            "it indicates a DI registration mistake — IUserManagement should resolve " +
            "to CachingUserService.");

    public async Task<ExpiredDeletionAnonymizationResult?> ApplyExpiredDeletionAnonymizationAsync(
        Guid userId, CancellationToken ct = default)
    {
        // Own data only; the cross-section cascade is IAccountDeletionService's.
        if (await repo.GetByIdAsync(userId, ct) is null)
            return null;

        await repo.RemoveAllUserEmailsForUserAndSaveAsync(userId, ct);
        return await repo.ApplyExpiredDeletionAnonymizationAsync(userId, ct);
    }

    /// <summary>Verified rows only, matching the address or its gmail/googlemail alternate.</summary>
    /// <remarks>
    /// Verified-only is load-bearing: Google sync asks which account owns an address, and an
    /// unverified address must never resolve. The snapshot match is <c>CachingUserService</c>'s;
    /// repeating it here would re-derive the whole snapshot on every miss.
    /// </remarks>
    public Task<UserInfo?> GetByEmailOrAlternateAsync(string email, CancellationToken ct = default) =>
        infoReader.GetByEmailOrAlternateAsync(email, ct);

    public Task<IReadOnlyList<Guid>> GetAccountsDueForAnonymizationAsync(
        Instant now, CancellationToken ct = default) =>
        repo.GetAccountsDueForAnonymizationAsync(now, ct);

    public async Task SetPreferredLanguageAsync(Guid userId, string preferredLanguage, CancellationToken ct = default)
    {
        await repo.SetPreferredLanguageAsync(userId, preferredLanguage, ct);
    }

    public async Task RecordLoginAsync(Guid userId, CancellationToken ct = default)
    {
        await repo.SetLastLoginAsync(userId, clock.GetCurrentInstant(), ct);
    }

    public async Task MarkFakeAsync(Guid userId, Guid actorUserId, string reason, CancellationToken ct = default)
    {
        if (await repo.SetMarkedFakeAsync(userId, clock.GetCurrentInstant(), ct))
            await audit.LogAsync(UsersAuditActions.AccountMarkedFake, nameof(User), userId, reason, actorUserId);
    }

    public Task<bool> SetDeletionPendingAsync(
        Guid userId, Instant requestedAt, Instant scheduledFor, Instant? eligibleAfter,
        CancellationToken ct = default) =>
        repo.SetDeletionPendingAsync(userId, requestedAt, scheduledFor, eligibleAfter, ct);

    public Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default) =>
        repo.ClearDeletionAsync(userId, ct);

    public Task<bool> EnsureStubProfileAsync(
        Guid userId,
        string? burnerName = null,
        string? firstName = null,
        string? lastName = null,
        CancellationToken ct = default,
        Guid? actorUserId = null) =>
        profileMutations.EnsureStubProfileAsync(userId, burnerName, firstName, lastName, ct, actorUserId);

    public async Task<OnboardingResult> SetSuspensionAsync(
        Guid userId, bool suspended, bool adminSuspension = false, CancellationToken ct = default)
    {
        if (await repo.GetByUserIdAsync(userId, ct) is null)
            return new OnboardingResult(false, "NotFound");

        // users.State alone: the reason goes to the audit log and the notification, not the row.
        await repo.SetSuspensionAsync(userId, suspended, adminSuspension, ct);
        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> SetRejectedAsync(Guid userId, CancellationToken ct = default)
    {
        if (await repo.GetByUserIdAsync(userId, ct) is null)
            return new OnboardingResult(false, "NotFound");

        // users.State alone: the reason, time and reviewer are Consent's review.
        await repo.SetRejectedAsync(userId, ct);
        return new OnboardingResult(true);
    }

    public Task<UserProfileSaveResult> SaveProfileAsync(
        Guid userId,
        UserProfileSaveCommand command,
        byte[]? profilePictureData = null,
        CancellationToken ct = default,
        IReadOnlyList<CVEntry>? volunteerHistory = null) =>
        profileMutations.SaveProfileAsync(userId, command, profilePictureData, ct, volunteerHistory);

    public Task SaveMedicalConditionsAsync(
        Guid userId, string? medicalConditions, CancellationToken ct = default) =>
        profileMutations.SaveMedicalConditionsAsync(userId, medicalConditions, ct);

    internal async Task<UserProfileAnonymizeResult> AnonymizeProfileForDeletionAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        return new UserProfileAnonymizeResult(await repo.AnonymizeForDeletionByUserIdAsync(userId, ct));
    }

    public Task<UserProfileLanguagesSaveResult> SaveProfileLanguagesAsync(
        Guid profileId, IReadOnlyList<ProfileLanguageInfo> languages, CancellationToken ct = default) =>
        profileMutations.SaveProfileLanguagesAsync(profileId, languages, ct);

    public Task<IReadOnlySet<Guid>> SuspendProfilesForMissingConsentAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default) =>
        repo.SuspendManyAsync(userIds, ct);

    public Task<UserEmailAddResult> AddUserEmailAsync(
        Guid userId, UserEmailAddCommand command, CancellationToken ct = default) =>
        emailMutations.AddUserEmailAsync(userId, command, ct);

    public Task<UpdateEmailResult> UpdateUserEmailAsync(
        Guid userId, Guid emailId, UserEmailUpdateCommand command, CancellationToken ct = default) =>
        emailMutations.UpdateUserEmailAsync(userId, emailId, command, ct);

    public Task<bool> RemoveUserEmailAsync(
        Guid userId, Guid emailId, UserEmailRemoveCommand command, CancellationToken ct = default) =>
        emailMutations.RemoveUserEmailAsync(userId, emailId, command, ct);

    public async Task<UserEmailReconcilePlanResult> ApplyUserEmailReconcilePlanAsync(
        Guid userId,
        UserEmailReconcilePlanCommand command,
        CancellationToken ct = default)
    {
        var user = await repo.GetByIdAsync(userId, ct)
            ?? throw new InvalidOperationException("User not found.");
        if (UserStateEvaluator.IsTombstone(user))
            throw new InvalidOperationException("Cannot reconcile email addresses for a deleted or merged account.");

        await repo.ApplyUserEmailReconcilePlanAsync(
            displacedRowToDelete: command.DisplacedRowToDelete,
            rowToDelete: command.RowToDelete,
            rowToUpdate: command.RowToUpdate,
            rowToInsert: command.RowToInsert,
            ct);

        var mutatedUserIds = new HashSet<Guid> { userId };
        if (command.DisplacedRowToDelete is not null)
            mutatedUserIds.Add(command.DisplacedRowToDelete.UserId);

        foreach (var mutatedUserId in mutatedUserIds)
        {
            await emailMutations.EnsurePrimaryInvariantAsync(mutatedUserId, ct);
        }

        return new UserEmailReconcilePlanResult(mutatedUserIds);
    }

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        dataExport.ContributeForUserAsync(userId, ct);

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [Account] =
                "Partially retained: the User row survives as a tombstone (its id, the display " +
                "name \"Deleted User\" and a deleted-<id>@deleted.local address) so every other " +
                "section's user-id reference stays resolvable and the erasure itself remains " +
                "provable — GDPR Art. 5(2) accountability. Every identity field on it is " +
                "overwritten and the account is locked out permanently.",
            [Profile] = null,
            [MergedProfiles] = null,
            [ContactFields] = null,
            [UserEmails] = null,
            [VolunteerHistory] = null,
            [Languages] = null
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>Owns the account record: erased after every other contributor.</summary>
    public bool ErasesLast => true;

    public async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        var profile = await repo.GetByUserIdReadOnlyAsync(userId, ct);
        if (profile is not null && ProfilePictureKeys.Resolve(
                profile.Id, profile.ProfilePictureContentType, profile.ProfilePictureStorageKey) is { } pictureKey)
        {
            // Retain the only retryable file reference until deletion succeeds.
            await fileStorage.DeleteAsync(pictureKey, ct);
        }

        await AnonymizeProfileForDeletionAsync(userId, ct);
        await repo.EraseProfileExtrasForUserAsync(userId, ct);
        await ApplyExpiredDeletionAnonymizationAsync(userId, ct);
    }

    public async Task<bool> AnonymizeForMergeAsync(
        Guid sourceUserId, Guid targetUserId, Instant now,
        CancellationToken ct = default)
    {
        return await repo.AnonymizeForMergeAsync(sourceUserId, targetUserId, now, ct);
    }

    public async Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now,
        CancellationToken ct)
    {
        _ = actorUserId;
        await repo.ReassignLoginsToUserAsync(mergedFromUserId, mergedToUserId, ct);
        var previousPicture = await repo.ReassignSubAggregatesToUserAsync(mergedFromUserId, mergedToUserId, now, ct);
        if (previousPicture is { } picture &&
            ProfilePictureKeys.Resolve(picture.ProfileId, picture.ContentType, picture.StorageKey) is { } key)
        {
            await profileMutations.DeleteSupersededPictureAsync(picture.ProfileId, key);
        }
    }

    public async Task<IReadOnlyList<Guid>> GetUsersWithLoginsButNoEmailsAsync(CancellationToken ct = default)
    {
        var loginUserIds = await repo.GetUserIdsWithExternalLoginsAsync(ct);
        if (loginUserIds.Count == 0)
            return [];

        var withEmail = await repo.GetUserIdsHavingAnyUserEmailAsync(loginUserIds, ct);
        var withEmailSet = withEmail.ToHashSet();
        return loginUserIds.Where(id => !withEmailSet.Contains(id)).ToList();
    }

    public async Task<int> DeleteUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default)
    {
        await adminAuthorization.RequireCurrentUserIsAdminAsync(ct);
        await repo.RemoveAllUserEmailsForUsersAndSaveAsync(userIds, ct);
        return await repo.DeleteUsersAsync(userIds, ct);
    }

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>>
        GetExternalLoginsByUserIdsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        repo.GetExternalLoginsByUserIdsAsync(userIds, ct);
}

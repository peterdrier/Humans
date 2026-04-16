using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.Interfaces;

public record ProfileSaveRequest(
    string BurnerName, string FirstName, string LastName,
    string? City, string? CountryCode, double? Latitude, double? Longitude, string? PlaceId,
    string? Bio, string? Pronouns, string? ContributionInterests, string? BoardNotes,
    int? BirthdayMonth, int? BirthdayDay,
    string? EmergencyContactName, string? EmergencyContactPhone, string? EmergencyContactRelationship,
    bool NoPriorBurnExperience,
    byte[]? ProfilePictureData, string? ProfilePictureContentType, bool RemoveProfilePicture,
    MembershipTier? SelectedTier, string? ApplicationMotivation, string? ApplicationAdditionalInfo,
    string? ApplicationSignificantContribution, string? ApplicationRoleUnderstanding);

public record CachedProfile(
    Guid UserId, string DisplayName, string? ProfilePictureUrl,
    bool HasCustomPicture, Guid ProfileId, long UpdatedAtTicks,
    string? BurnerName, string? Bio, string? Pronouns,
    string? ContributionInterests,
    string? City, string? CountryCode, double? Latitude, double? Longitude,
    int? BirthdayDay, int? BirthdayMonth,
    bool IsApproved, bool IsSuspended,
    IReadOnlyList<CachedVolunteerEntry> VolunteerHistory,
    string? NotificationEmail = null)
{
    public static CachedProfile Create(Profile profile, User user, string? notificationEmail = null) => new(
        UserId: user.Id,
        DisplayName: user.DisplayName,
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
        VolunteerHistory: profile.VolunteerHistory
            .Select(v => new CachedVolunteerEntry(v.EventName, v.Description))
            .ToList(),
        NotificationEmail: notificationEmail);
}

public record CachedVolunteerEntry(string EventName, string? Description);

public interface IProfileService
{
    Task<Profile?> GetProfileAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Batched profile fetch keyed by user id. Missing users are absent
    /// from the returned dictionary. Used by cross-section services that
    /// need to stitch profile slices in memory instead of pulling them
    /// through a cross-domain <c>.Include</c> chain.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Profile>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default);

    /// <summary>
    /// Updates the profile's <see cref="Profile.MembershipTier"/> and
    /// <see cref="Profile.UpdatedAt"/>, persists, and invalidates the
    /// profile's cache entry. No-op with a warning log if the user has no
    /// profile. Used by governance services that previously mutated the
    /// profile directly through a cross-domain navigation property.
    /// </summary>
    Task SetMembershipTierAsync(
        Guid userId,
        MembershipTier tier,
        CancellationToken ct = default);

    Task<(Profile? Profile, MemberApplication? LatestApplication, int PendingConsentCount)>
        GetProfileIndexDataAsync(Guid userId, CancellationToken ct = default);
    Task<(Profile? Profile, bool IsTierLocked, MemberApplication? PendingApplication)>
        GetProfileEditDataAsync(Guid userId, CancellationToken ct = default);
    Task<(byte[]? Data, string? ContentType)> GetProfilePictureAsync(Guid profileId, CancellationToken ct = default);
    Task<Guid> SaveProfileAsync(Guid userId, string displayName, ProfileSaveRequest request, string language, CancellationToken ct = default);
    Task<OnboardingResult> RequestDeletionAsync(Guid userId, CancellationToken ct = default);
    Task<OnboardingResult> CancelDeletionAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns a post-event hold date if the user has tickets for the active event,
    /// or null if no hold applies.
    /// </summary>
    Task<Instant?> GetEventHoldDateAsync(Guid userId, CancellationToken ct = default);
    Task<(bool CanAdd, int MinutesUntilResend, Guid? PendingEmailId)>
        GetEmailCooldownInfoAsync(Guid pendingEmailId, CancellationToken ct = default);

    Task<(int ColaboradorCount, int AsociadoCount)> GetTierCountsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<(Guid ProfileId, Guid UserId, long UpdatedAtTicks)>>
        GetCustomPictureInfoByUserIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct = default);

    Task<IReadOnlyList<CampaignGrant>> GetActiveOrCompletedCampaignGrantsAsync(
        Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<DTOs.BirthdayProfileInfo>>
        GetBirthdayProfilesAsync(int month, CancellationToken ct = default);

    Task<IReadOnlyList<DTOs.LocationProfileInfo>>
        GetApprovedProfilesWithLocationAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DTOs.AdminHumanRow>> GetFilteredHumansAsync(
        string? search, string? statusFilter, CancellationToken ct = default);

    Task<DTOs.AdminHumanDetailData?> GetAdminHumanDetailAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<UserSearchResult>> SearchApprovedUsersAsync(string query, CancellationToken ct = default);

    Task<IReadOnlyList<HumanSearchResult>> SearchHumansAsync(string query, CancellationToken ct = default);

    /// <summary>
    /// Invalidates any cached profile data for the given user. Used by
    /// cross-section services that modify profile-related data and need
    /// to ensure the next read returns fresh results.
    /// </summary>
    Task InvalidateCacheAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Gets the languages associated with a profile, ordered by proficiency (descending) then language code.
    /// Returns an empty list if the profile does not exist.
    /// </summary>
    Task<IReadOnlyList<ProfileLanguage>> GetProfileLanguagesAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Replaces all languages for the given profile with the new set.
    /// </summary>
    Task SaveProfileLanguagesAsync(Guid profileId, IReadOnlyList<ProfileLanguage> languages, CancellationToken ct = default);

}

public record UserSearchResult(Guid UserId, string DisplayName, string Email);

public record HumanSearchResult(
    Guid UserId,
    string DisplayName,
    string? BurnerName,
    string? City,
    string? Bio,
    string? ContributionInterests,
    string? ProfilePictureUrl,
    bool HasCustomPicture,
    Guid ProfileId,
    long UpdatedAtTicks,
    string? MatchField,
    string? MatchSnippet);

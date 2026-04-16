using Humans.Domain.Entities;

namespace Humans.Application.Interfaces.Stores;

/// <summary>
/// Cached representation of a <see cref="Profile"/> + its owning <see cref="User"/>,
/// denormalized for in-memory search and listing. Implementation detail of
/// <see cref="IProfileStore"/> and the <c>CachingProfileService</c> decorator.
/// </summary>
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

/// <summary>
/// In-memory canonical store for <see cref="CachedProfile"/> entries,
/// keyed by <c>UserId</c>. See <c>docs/architecture/design-rules.md</c> §4
/// for the store pattern.
/// </summary>
/// <remarks>
/// Warmed at startup via <c>IProfileRepository.GetAllAsync()</c> combined
/// with <c>IUserService.GetByIdsAsync()</c>; at ~500-user scale the full
/// set fits in memory trivially. Replaces the old
/// <c>CacheKeys.Profiles</c> <c>IMemoryCache</c> entry.
/// </remarks>
public interface IProfileStore
{
    CachedProfile? GetByUserId(Guid userId);

    /// <summary>
    /// Snapshot of all cached profiles in the store.
    /// </summary>
    IReadOnlyList<CachedProfile> GetAll();

    /// <summary>
    /// Inserts or replaces a cached profile keyed by <c>userId</c>.
    /// </summary>
    void Upsert(Guid userId, CachedProfile profile);

    /// <summary>
    /// Removes a cached profile by user id. No-op if not present.
    /// </summary>
    void Remove(Guid userId);

    /// <summary>
    /// Replaces the entire contents of the store. Used by the startup
    /// warmup hosted service to populate the store once from the
    /// repositories.
    /// </summary>
    void LoadAll(IReadOnlyDictionary<Guid, CachedProfile> profiles);
}

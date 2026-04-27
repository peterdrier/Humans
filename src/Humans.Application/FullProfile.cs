using Humans.Domain.Entities;

namespace Humans.Application;

/// <summary>
/// Denormalized profile projection used by the caching decorator and its
/// consumers (avatar, link tag helper, profile card). Stitched from
/// <see cref="Profile"/>, the owning <see cref="User"/>, and the profile's
/// CV entries.
/// </summary>
public record FullProfile(
    Guid UserId, string DisplayName, string? ProfilePictureUrl,
    bool HasCustomPicture, Guid ProfileId, long UpdatedAtTicks,
    string? BurnerName, string? Bio, string? Pronouns,
    string? ContributionInterests,
    string? PersonalBoundaries,
    string? City, string? CountryCode, double? Latitude, double? Longitude,
    int? BirthdayDay, int? BirthdayMonth,
    bool IsApproved, bool IsSuspended,
    IReadOnlyList<CVEntry> CVEntries,
    string? NotificationEmail = null)
{
    /// <summary>
    /// Overload that accepts an explicit volunteer-history list, for callers that
    /// already have the history loaded separately and must not mutate the profile entity.
    /// </summary>
    public static FullProfile Create(
        Profile profile,
        User user,
        IReadOnlyList<VolunteerHistoryEntry> volunteerHistory,
        string? notificationEmail = null) => new(
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
            PersonalBoundaries: profile.PersonalBoundaries,
            City: profile.City,
            CountryCode: profile.CountryCode,
            Latitude: profile.Latitude,
            Longitude: profile.Longitude,
            BirthdayDay: profile.DateOfBirth?.Day,
            BirthdayMonth: profile.DateOfBirth?.Month,
            IsApproved: profile.IsApproved,
            IsSuspended: profile.IsSuspended,
            CVEntries: volunteerHistory
                .OrderByDescending(v => v.Date)
                .Select(v => new CVEntry(v.Id, v.Date, v.EventName, v.Description))
                .ToList(),
            NotificationEmail: notificationEmail);

    public static FullProfile Create(Profile profile, User user, string? notificationEmail = null) =>
        Create(profile, user, profile.VolunteerHistory.ToList(), notificationEmail);
}

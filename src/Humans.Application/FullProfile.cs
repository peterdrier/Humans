using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application;

/// <summary>
/// Denormalized profile projection used by the caching decorator and its
/// consumers (avatar, link tag helper, profile card). Stitched from
/// <see cref="Profile"/>, the owning <see cref="User"/>, and the profile's
/// CV entries plus loaded UserEmails.
/// </summary>
/// <remarks>
/// Issue #635 (§15i): the canonical "everything-about-a-person" read path.
/// <see cref="PrimaryEmail"/>, <see cref="AllVerifiedEmails"/>, and
/// <see cref="GoogleEmail"/> replace the old <c>User.UserEmails</c> /
/// <c>User.GetEffectiveEmail()</c> reader sites. <see cref="State"/> reflects
/// the lifecycle marker on Profile (lazily populated).
/// </remarks>
public record FullProfile(
    Guid UserId, string DisplayName, string? ProfilePictureUrl,
    bool HasCustomPicture, Guid ProfileId, long UpdatedAtTicks,
    string? BurnerName, string? Bio, string? Pronouns,
    string? ContributionInterests,
    string? City, string? CountryCode, double? Latitude, double? Longitude,
    int? BirthdayDay, int? BirthdayMonth,
    bool IsApproved, bool IsSuspended,
    IReadOnlyList<CVEntry> CVEntries,
    string? PrimaryEmail = null,
    IReadOnlyList<string>? AllVerifiedEmails = null,
    string? GoogleEmail = null,
    ProfileState? State = null)
{
    /// <summary>
    /// Backward-compat alias for the renamed <see cref="PrimaryEmail"/>. Kept
    /// because external code (and a few in-PR holdovers) still read the old
    /// name; both expose the same value. Issue #635 (§15i).
    /// </summary>
    public string? NotificationEmail => PrimaryEmail;

    /// <summary>
    /// Issue #635 (§15i): defensive non-null projection of
    /// <see cref="AllVerifiedEmails"/> for callers that don't want to handle
    /// the nullable. Returns the empty list when no UserEmails were loaded.
    /// </summary>
    public IReadOnlyList<string> VerifiedEmails =>
        AllVerifiedEmails ?? Array.Empty<string>();

    /// <summary>
    /// Overload that accepts an explicit volunteer-history list and the loaded
    /// UserEmails for the user, populating <see cref="PrimaryEmail"/>,
    /// <see cref="AllVerifiedEmails"/>, and <see cref="GoogleEmail"/> from the
    /// already-loaded data — no additional repository lookups.
    /// </summary>
    public static FullProfile Create(
        Profile profile,
        User user,
        IReadOnlyList<VolunteerHistoryEntry> volunteerHistory,
        IReadOnlyList<UserEmail> userEmails)
    {
        var primary = userEmails
            .Where(e => e.IsPrimary && e.IsVerified)
            .Select(e => e.Email)
            .FirstOrDefault();

        var verified = userEmails
            .Where(e => e.IsVerified)
            .Select(e => e.Email)
            .ToList();

        var google = userEmails
            .Where(e => e.IsGoogle && e.IsVerified)
            .Select(e => e.Email)
            .FirstOrDefault();

#pragma warning disable HUM_PROFILE_ISSUSPENDED
        var isSuspended = profile.IsSuspended;
#pragma warning restore HUM_PROFILE_ISSUSPENDED

        return new FullProfile(
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
            IsSuspended: isSuspended,
            CVEntries: volunteerHistory
                .OrderByDescending(v => v.Date)
                .Select(v => new CVEntry(v.Id, v.Date, v.EventName, v.Description))
                .ToList(),
            PrimaryEmail: primary,
            AllVerifiedEmails: verified,
            GoogleEmail: google,
            State: profile.State);
    }

    /// <summary>
    /// Legacy overload: creates a FullProfile with only the primary email
    /// known. <see cref="AllVerifiedEmails"/> and <see cref="GoogleEmail"/>
    /// will be empty/null. Used by call sites that pre-date the userEmails
    /// parameter and only resolved a single notification address.
    /// </summary>
    public static FullProfile Create(
        Profile profile,
        User user,
        IReadOnlyList<VolunteerHistoryEntry> volunteerHistory,
        string? notificationEmail = null)
    {
#pragma warning disable HUM_PROFILE_ISSUSPENDED
        var isSuspended = profile.IsSuspended;
#pragma warning restore HUM_PROFILE_ISSUSPENDED

        return new FullProfile(
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
            IsSuspended: isSuspended,
            CVEntries: volunteerHistory
                .OrderByDescending(v => v.Date)
                .Select(v => new CVEntry(v.Id, v.Date, v.EventName, v.Description))
                .ToList(),
            PrimaryEmail: notificationEmail,
            AllVerifiedEmails: notificationEmail is null
                ? Array.Empty<string>()
                : new[] { notificationEmail },
            GoogleEmail: null,
            State: profile.State);
    }

    public static FullProfile Create(Profile profile, User user, string? notificationEmail = null) =>
        Create(profile, user, profile.VolunteerHistory.ToList(), notificationEmail);
}

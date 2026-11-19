using Humans.Users.Contracts;

using NodaTime;

namespace Humans.Testing;

/// <summary>Every parameter is defaulted: <c>UserFixtures.Profile(city: "Berlin")</c>.</summary>
public static class UserFixtures
{
    public static readonly Instant DefaultInstant = Instant.FromUtc(2026, 1, 1, 0, 0);

    /// <summary>
    /// The stored <c>User.State</c> for <paramref name="profile"/>. Nothing derives state on read, so a test that builds
    /// a <c>UserInfo</c> by hand must stamp it.
    /// </summary>
    public static UserState StateFor(ProfileInfo? profile, string? burnerName, string? firstName, string? lastName) =>
        profile is not null
        && !string.IsNullOrWhiteSpace(burnerName)
        && !string.IsNullOrWhiteSpace(firstName)
        && !string.IsNullOrWhiteSpace(lastName)
            ? UserState.Active
            : UserState.Bare;

    /// <summary>A named user with a profile. <c>UserInfo.BurnerName</c> reads <c>User.BurnerName</c>, so it is seeded there.</summary>
    public static UserInfo MakeUserInfo(Guid userId, ProfileInfo? profile = null, string displayName = "User") =>
        UserInfoFixtures.FromUser(new User { Id = userId, PreferredLanguage = "en", BurnerName = displayName }, [], profile ?? Profile());

    public static ProfileInfo Profile(
        Guid? id = null,
        string? city = null,
        string? countryCode = null,
        double? latitude = null,
        double? longitude = null,
        string? placeId = null,
        string? bio = null,
        string? pronouns = null,
        int? birthdayDay = null,
        int? birthdayMonth = null,
        string? emergencyContactName = null,
        string? emergencyContactPhone = null,
        string? emergencyContactRelationship = null,
        string? profilePictureContentType = null,
        Instant? createdAt = null,
        Instant? updatedAt = null,
        string? adminNotes = null,
        string? contributionInterests = null,
        string? boardNotes = null,
        bool noPriorBurnExperience = false,
        IReadOnlyList<ContactFieldInfo>? contactFields = null,
        IReadOnlyList<ProfileLanguageInfo>? languages = null,
        IReadOnlyList<VolunteerHistoryInfo>? volunteerHistory = null) =>
        new(
            Id: id ?? Guid.NewGuid(),
            City: city,
            CountryCode: countryCode,
            Latitude: latitude,
            Longitude: longitude,
            PlaceId: placeId,
            Bio: bio,
            Pronouns: pronouns,
            BirthdayDay: birthdayDay,
            BirthdayMonth: birthdayMonth,
            EmergencyContactName: emergencyContactName,
            EmergencyContactPhone: emergencyContactPhone,
            EmergencyContactRelationship: emergencyContactRelationship,
            // Derived as UserInfoFactory does, so no fixture can have a picture without a content type.
            HasCustomPicture: profilePictureContentType is not null,
            ProfilePictureContentType: profilePictureContentType,
            CreatedAt: createdAt ?? DefaultInstant,
            UpdatedAt: updatedAt ?? createdAt ?? DefaultInstant,
            AdminNotes: adminNotes,
            ContributionInterests: contributionInterests,
            BoardNotes: boardNotes,
            NoPriorBurnExperience: noPriorBurnExperience,
            ContactFields: contactFields ?? [],
            Languages: languages ?? [],
            VolunteerHistory: volunteerHistory ?? []);

    public static ProfileLanguageInfo Language(
        string languageCode = "en",
        LanguageProficiency proficiency = LanguageProficiency.Fluent,
        Guid? id = null) =>
        new(id ?? Guid.NewGuid(), languageCode, proficiency);

    public static ContactFieldInfo ContactField(
        ContactFieldType fieldType = ContactFieldType.Phone,
        string value = "",
        ContactFieldVisibility visibility = ContactFieldVisibility.BoardOnly,
        string? customLabel = null,
        int displayOrder = 0,
        Guid? id = null) =>
        new(id ?? Guid.NewGuid(), fieldType, customLabel, value, visibility, displayOrder);

    public static VolunteerHistoryInfo VolunteerHistory(
        LocalDate? date = null,
        string eventName = "Event",
        string? description = null,
        Guid? id = null) =>
        new(id ?? Guid.NewGuid(), date ?? new LocalDate(2026, 1, 1), eventName, description);
}

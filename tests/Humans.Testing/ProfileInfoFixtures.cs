using Humans.Users.Contracts;

using NodaTime;

namespace Humans.Testing;

/// <summary>
/// Builds <see cref="ProfileInfo"/> and its nested projections for tests that stub
/// <c>IUserService</c> / <c>IUserService</c>.
/// </summary>
/// <remarks>
/// Same reason as <see cref="ShiftFixtures"/>: <see cref="ProfileInfo"/> is positional with
/// forty-four members and is built from twenty-nine section test projects, so every parameter is
/// defaulted and a test that only cares about the burner name writes
/// <c>UserFixtures.Profile(burnerName: "Alice")</c>. It replaces constructing the
/// <c>Profile</c> row, which became internal to <c>Humans.Users</c>
/// (nobodies-collective/Humans#1051).
/// </remarks>
public static class UserFixtures
{
    /// <summary>The instant every fixture profile is created/updated at unless overridden.</summary>
    public static readonly Instant DefaultInstant = Instant.FromUtc(2026, 1, 1, 0, 0);

    /// <summary>
    /// The stored <c>User.State</c> a row with <paramref name="profile"/> carries: Bare until the
    /// required name fields are filled. Nothing derives state on
    /// read any more, so a test that builds a <c>UserInfo</c> by hand must stamp it.
    /// </summary>
    public static UserState StateFor(ProfileInfo? profile) =>
        profile switch
        {
            not null when !string.IsNullOrWhiteSpace(profile.BurnerName)
                && !string.IsNullOrWhiteSpace(profile.FirstName)
                && !string.IsNullOrWhiteSpace(profile.LastName) => UserState.Active,
            _ => UserState.Bare,
        };

    public static ProfileInfo Profile(
        Guid? id = null,
        string burnerName = "",
        string firstName = "",
        string lastName = "",
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
        string? medicalConditions = null,
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
            BurnerName: burnerName,
            FirstName: firstName,
            LastName: lastName,
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
            MedicalConditions: medicalConditions,
            // Derived exactly as UserInfoFactory derives it from the entity, so no fixture
            // can build the impossible "has a picture with no content type" combination.
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

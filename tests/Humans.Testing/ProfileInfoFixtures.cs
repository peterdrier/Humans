using Humans.Users.Contracts;

using NodaTime;

namespace Humans.Testing;

/// <summary>Every parameter is defaulted: <c>UserFixtures.Profile(burnerName: "Alice")</c>.</summary>
public static class UserFixtures
{
    public static readonly Instant DefaultInstant = Instant.FromUtc(2026, 1, 1, 0, 0);

    /// <summary>
    /// The stored <c>User.State</c> for <paramref name="profile"/>. Nothing derives state on read, so a test that builds
    /// a <c>UserInfo</c> by hand must stamp it.
    /// </summary>
    public static UserState StateFor(ProfileInfo? profile) =>
        profile switch
        {
#pragma warning disable HUM_PROFILE_REVIEW // a legacy-rejected fixture profile still classifies as Rejected
            { RejectedAt: not null } => UserState.Rejected,
#pragma warning restore HUM_PROFILE_REVIEW
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
        string? iban = null,
        bool isApproved = false,
        LegacyMembershipTier membershipTier = LegacyMembershipTier.Volunteer,
        LegacyConsentCheckStatus? consentCheckStatus = null,
        Instant? consentCheckAt = null,
        Guid? consentCheckedByUserId = null,
        string? consentCheckNotes = null,
        string? rejectionReason = null,
        Instant? rejectedAt = null,
        Guid? rejectedByUserId = null,
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
            // Derived as UserInfoFactory does, so no fixture can have a picture without a content type.
            HasCustomPicture: profilePictureContentType is not null,
            ProfilePictureContentType: profilePictureContentType,
            CreatedAt: createdAt ?? DefaultInstant,
            UpdatedAt: updatedAt ?? createdAt ?? DefaultInstant,
            AdminNotes: adminNotes,
            ContributionInterests: contributionInterests,
            BoardNotes: boardNotes,
            Iban: iban,
            IsApproved: isApproved,
            MembershipTier: membershipTier,
            ConsentCheckStatus: consentCheckStatus,
            ConsentCheckAt: consentCheckAt,
            ConsentCheckedByUserId: consentCheckedByUserId,
            ConsentCheckNotes: consentCheckNotes,
            RejectionReason: rejectionReason,
            RejectedAt: rejectedAt,
            RejectedByUserId: rejectedByUserId,
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

    /// <summary>A frozen legacy Users preference row; Email owns the live ones.</summary>
    public static LegacyCommunicationPreferenceInfo Preference(
        LegacyMessageCategory category = LegacyMessageCategory.System,
        bool optedOut = false,
        bool inboxEnabled = true,
        Instant? updatedAt = null,
        string updateSource = "test",
        Instant? subscribedAt = null,
        Guid? id = null) =>
        new(id ?? Guid.NewGuid(), category, optedOut, inboxEnabled,
            updatedAt ?? DefaultInstant, updateSource, subscribedAt);
}

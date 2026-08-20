using Humans.Users.Contracts;

namespace Humans.Users.Services;

/// <summary>
/// Projects the section's entity graph onto <see cref="UserInfo"/>. The six Profile-side entities
/// are internal to this assembly (nobodies-collective/Humans#1051), so the entity-taking factory
/// lives here and <see cref="UserInfo.Create"/> keeps only the primitive-only overload.
/// </summary>
internal static class UserInfoFactory
{
    /// <summary>Builds <see cref="UserInfo"/> from the 8 contributing tables.</summary>
    public static UserInfo Create(
        User user,
        IReadOnlyList<UserEmail> userEmails,
        IReadOnlyList<EventParticipation> eventParticipations,
        IReadOnlyList<(string Provider, string ProviderKey)> externalLogins,
        Profile? profile,
        IReadOnlyList<ContactField> contactFields,
        IReadOnlyList<ProfileLanguage> profileLanguages,
        IReadOnlyList<VolunteerHistoryEntry> volunteerHistory,
        IReadOnlyList<CommunicationPreference> communicationPreferences) =>
        UserInfo.Create(
            user,
            userEmails,
            eventParticipations,
            externalLogins,
            ToProfileInfo(profile, contactFields, profileLanguages, volunteerHistory),
            communicationPreferences
                .Select(c => new CommunicationPreferenceInfo(
                    c.Id, c.Category, c.OptedOut, c.InboxEnabled,
                    c.UpdatedAt, c.UpdateSource, c.SubscribedAt))
                .ToList());

    private static ProfileInfo? ToProfileInfo(
        Profile? profile,
        IReadOnlyList<ContactField> contactFields,
        IReadOnlyList<ProfileLanguage> profileLanguages,
        IReadOnlyList<VolunteerHistoryEntry> volunteerHistory)
    {
        if (profile is null)
            return null;

        var contactFieldInfos = contactFields
            .OrderBy(c => c.DisplayOrder)
            .Select(c => new ContactFieldInfo(
                c.Id, c.FieldType, c.CustomLabel, c.Value, c.Visibility, c.DisplayOrder))
            .ToList();

        var languageInfos = profileLanguages
            .OrderByDescending(l => l.Proficiency)
            .ThenBy(l => l.LanguageCode, StringComparer.OrdinalIgnoreCase)
            .Select(l => new ProfileLanguageInfo(l.Id, l.LanguageCode, l.Proficiency))
            .ToList();

        var volunteerHistoryInfos = volunteerHistory
            .OrderByDescending(v => v.Date)
            .Select(v => new VolunteerHistoryInfo(v.Id, v.Date, v.EventName, v.Description))
            .ToList();

        return new ProfileInfo(
            Id: profile.Id,
            BurnerName: profile.BurnerName,
            FirstName: profile.FirstName,
            LastName: profile.LastName,
            City: profile.City,
            CountryCode: profile.CountryCode,
            Latitude: profile.Latitude,
            Longitude: profile.Longitude,
            PlaceId: profile.PlaceId,
            Bio: profile.Bio,
            Pronouns: profile.Pronouns,
            BirthdayDay: profile.DateOfBirth?.Day,
            BirthdayMonth: profile.DateOfBirth?.Month,
            EmergencyContactName: profile.EmergencyContactName,
            EmergencyContactPhone: profile.EmergencyContactPhone,
            EmergencyContactRelationship: profile.EmergencyContactRelationship,
            DietaryPreference: profile.DietaryPreference,
            Allergies: profile.Allergies,
            AllergyOtherText: profile.AllergyOtherText,
            Intolerances: profile.Intolerances,
            IntoleranceOtherText: profile.IntoleranceOtherText,
            MedicalConditions: profile.MedicalConditions,
            HasCustomPicture: profile.ProfilePictureContentType is not null,
            ProfilePictureContentType: profile.ProfilePictureContentType,
            CreatedAt: profile.CreatedAt,
            UpdatedAt: profile.UpdatedAt,
            AdminNotes: profile.AdminNotes,
            ContributionInterests: profile.ContributionInterests,
            BoardNotes: profile.BoardNotes,
            Iban: profile.Iban,
            IsApproved: profile.IsApproved,
            MembershipTier: profile.MembershipTier,
            ConsentCheckStatus: profile.ConsentCheckStatus,
            ConsentCheckAt: profile.ConsentCheckAt,
            ConsentCheckedByUserId: profile.ConsentCheckedByUserId,
            ConsentCheckNotes: profile.ConsentCheckNotes,
            RejectionReason: profile.RejectionReason,
            RejectedAt: profile.RejectedAt,
            RejectedByUserId: profile.RejectedByUserId,
            NoPriorBurnExperience: profile.NoPriorBurnExperience,
            ContactFields: contactFieldInfos,
            Languages: languageInfos,
            VolunteerHistory: volunteerHistoryInfos);
    }
}

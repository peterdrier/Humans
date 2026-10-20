using Humans.Governance.Contracts;
using Humans.Base.Extensions;
using Humans.Users.Contracts;

namespace Humans.Users.Models;

internal static class ProfileEditViewModelBuilder
{
    public static ProfileViewModel Build(
        UserInfo info,
        IReadOnlyList<UserApplicationSnapshot> applications,
        bool preview,
        Func<ProfileInfo, string?> customPictureUrl)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(applications);
        ArgumentNullException.ThrowIfNull(customPictureUrl);

        var profile = info.Profile;

        var isTierLocked = profile is not null && applications.Any(a =>
            a.Status == ApplicationStatus.Submitted || a.Status == ApplicationStatus.Approved);
        var pendingApplication = profile is null || !profile.IsApproved
            ? applications.FirstOrDefault(a => a.Status == ApplicationStatus.Submitted)
            : null;
        var hasCustomPicture = profile?.HasCustomPicture == true;
        var isInitialSetup = profile is null || !profile.IsApproved || preview;

        return new ProfileViewModel
        {
            Id = profile?.Id ?? Guid.Empty,
            UserId = info.Id,
            Email = info.Email ?? string.Empty,
            DisplayName = info.BurnerName,
            ProfilePictureUrl = info.ProfilePictureUrl,
            HasCustomProfilePicture = hasCustomPicture,
            CustomProfilePictureUrl = hasCustomPicture && profile is not null
                ? customPictureUrl(profile)
                : null,
            BurnerName = profile?.BurnerName ?? info.BurnerName,
            FirstName = profile?.FirstName ?? string.Empty,
            LastName = profile?.LastName ?? string.Empty,
            City = profile?.City,
            CountryCode = profile?.CountryCode,
            Latitude = profile?.Latitude,
            Longitude = profile?.Longitude,
            PlaceId = profile?.PlaceId,
            Bio = profile?.Bio,
            Pronouns = profile?.Pronouns,
            ContributionInterests = profile?.ContributionInterests,
            BoardNotes = profile?.BoardNotes,
            BirthdayMonth = profile?.BirthdayMonth,
            BirthdayDay = profile?.BirthdayDay,
            EmergencyContactName = profile?.EmergencyContactName,
            EmergencyContactPhone = profile?.EmergencyContactPhone,
            EmergencyContactRelationship = profile?.EmergencyContactRelationship,
            CanViewLegalName = true,
            IsInitialSetup = isInitialSetup,
            SelectedTier = profile?.MembershipTier ?? MembershipTier.Volunteer,
            IsTierLocked = isTierLocked,
            ApplicationMotivation = pendingApplication?.Motivation,
            ApplicationAdditionalInfo = pendingApplication?.AdditionalInfo,
            ApplicationSignificantContribution = pendingApplication?.SignificantContribution,
            ApplicationRoleUnderstanding = pendingApplication?.RoleUnderstanding,
            NoPriorBurnExperience = profile?.NoPriorBurnExperience ?? false,
            ShowPrivateFirst = string.IsNullOrEmpty(profile?.FirstName)
                && string.IsNullOrEmpty(profile?.LastName)
                && string.IsNullOrEmpty(profile?.EmergencyContactName),
            ContactFields = BuildLegacyContactFields(profile),
            EditableContactFields = (profile?.ContactFields ?? []).Where(cf => cf.FieldType != default).Select(cf => new ContactFieldEditViewModel
            {
                Id = cf.Id,
                FieldType = cf.FieldType,
                CustomLabel = cf.CustomLabel,
                Value = cf.Value,
                Visibility = cf.Visibility,
                DisplayOrder = cf.DisplayOrder
            }).ToList(),
            EditableVolunteerHistory = (profile?.VolunteerHistory ?? []).Select(cv => new VolunteerHistoryEntryEditViewModel
            {
                Id = cv.Id,
                DateString = cv.Date.ToInvariantDate(),
                EventName = cv.EventName,
                Description = cv.Description
            }).ToList(),
            EditableLanguages = (profile?.Languages ?? []).Select(pl => new ProfileLanguageEditViewModel
            {
                Id = pl.Id,
                LanguageCode = pl.LanguageCode,
                Proficiency = pl.Proficiency
            }).ToList()
        };
    }

    public static IReadOnlyList<ContactFieldViewModel> BuildLegacyContactFields(ProfileInfo? profile)
        => (profile?.ContactFields ?? []).Where(cf => cf.FieldType == default).Select(cf => new ContactFieldViewModel
        {
            Id = cf.Id,
            FieldType = cf.FieldType,
            Value = cf.Value,
            Visibility = cf.Visibility
        }).ToList();
}

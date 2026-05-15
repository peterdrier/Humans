using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Extensions;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using ProfileEntity = Humans.Domain.Entities.Profile;

namespace Humans.Web.Models;

public static class ProfileEditViewModelBuilder
{
    public static ProfileViewModel Build(
        User user,
        ProfileEntity? profile,
        IReadOnlyList<UserApplicationSnapshot> applications,
        IReadOnlyList<ContactFieldEditDto> contactFields,
        IReadOnlyList<CVEntry> cvEntries,
        IReadOnlyList<ProfileLanguageSnapshot> languages,
        IReadOnlyList<ShiftTagSummary> allShiftTags,
        IReadOnlyList<ShiftTagPreferenceSummary> preferredShiftTags,
        bool preview,
        bool hasGoogleLogin,
        Func<ProfileEntity, string?> customPictureUrl)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(applications);
        ArgumentNullException.ThrowIfNull(contactFields);
        ArgumentNullException.ThrowIfNull(cvEntries);
        ArgumentNullException.ThrowIfNull(languages);
        ArgumentNullException.ThrowIfNull(allShiftTags);
        ArgumentNullException.ThrowIfNull(preferredShiftTags);
        ArgumentNullException.ThrowIfNull(customPictureUrl);

        var isTierLocked = profile is not null && applications.Any(a =>
            a.Status == ApplicationStatus.Submitted || a.Status == ApplicationStatus.Approved);
        var pendingApplication = profile is null || !profile.IsApproved
            ? applications.FirstOrDefault(a => a.Status == ApplicationStatus.Submitted)
            : null;
        var hasCustomPicture = profile?.HasCustomProfilePicture == true;
        var isInitialSetup = profile is null || !profile.IsApproved || preview;
        var canImportGooglePicture = hasGoogleLogin
            && !hasCustomPicture
            && !string.IsNullOrEmpty(user.ProfilePictureUrl);

        return new ProfileViewModel
        {
            Id = profile?.Id ?? Guid.Empty,
            UserId = user.Id,
            Email = user.Email ?? string.Empty,
            DisplayName = user.DisplayName,
            ProfilePictureUrl = user.ProfilePictureUrl,
            HasCustomProfilePicture = hasCustomPicture,
            CustomProfilePictureUrl = hasCustomPicture && profile is not null
                ? customPictureUrl(profile)
                : null,
            CanImportGooglePicture = canImportGooglePicture,
            BurnerName = profile?.BurnerName ?? user.DisplayName,
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
            BirthdayMonth = profile?.DateOfBirth?.Month,
            BirthdayDay = profile?.DateOfBirth?.Day,
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
            EditableContactFields = contactFields.Select(cf => new ContactFieldEditViewModel
            {
                Id = cf.Id,
                FieldType = cf.FieldType,
                CustomLabel = cf.CustomLabel,
                Value = cf.Value,
                Visibility = cf.Visibility,
                DisplayOrder = cf.DisplayOrder
            }).ToList(),
            EditableVolunteerHistory = cvEntries.Select(cv => new VolunteerHistoryEntryEditViewModel
            {
                Id = cv.Id,
                DateString = cv.Date.ToIsoDateString(),
                EventName = cv.EventName,
                Description = cv.Description
            }).ToList(),
            EditableLanguages = languages.Select(pl => new ProfileLanguageEditViewModel
            {
                Id = pl.Id,
                LanguageCode = pl.LanguageCode,
                Proficiency = pl.Proficiency
            }).ToList(),
            AllShiftTags = allShiftTags
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            EditableShiftTagIds = preferredShiftTags.Select(t => t.Id).ToList()
        };
    }
}

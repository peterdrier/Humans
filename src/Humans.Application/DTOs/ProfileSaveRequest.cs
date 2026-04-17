using Humans.Domain.Enums;

namespace Humans.Application.DTOs;

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

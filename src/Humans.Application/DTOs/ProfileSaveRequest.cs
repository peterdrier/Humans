namespace Humans.Application.DTOs;

public record ProfileSaveRequest(
    string BurnerName, string FirstName, string LastName,
    string? City, string? CountryCode, double? Latitude, double? Longitude, string? PlaceId,
    string? Bio, string? Pronouns, string? ContributionInterests, string? BoardNotes,
    int? BirthdayMonth, int? BirthdayDay,
    string? EmergencyContactName, string? EmergencyContactPhone, string? EmergencyContactRelationship,
    bool NoPriorBurnExperience,
    byte[]? ProfilePictureData, string? ProfilePictureContentType, bool RemoveProfilePicture,
    // Meal-pref + allergies owned by the Edit page (the DietaryMedical page owns
    // intolerances + medical via SaveDietaryMedicalAsync). These three only.
    string? DietaryPreference = null, List<string>? Allergies = null, string? AllergyOtherText = null,
    // Burner CV. Null = leave stored history untouched (name-only saves); a list —
    // including an empty one — is a full replace and is validated against
    // NoPriorBurnExperience (entries OR the flag).
    IReadOnlyList<CVEntry>? VolunteerHistory = null);

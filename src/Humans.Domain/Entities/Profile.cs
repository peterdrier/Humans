using Microsoft.AspNetCore.Identity;
using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>Member profile. MembershipStatus is computed from RoleAssignments and ConsentRecords.</summary>
public class Profile
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    /// <summary>Primary display name — burner name / nickname.</summary>
    [PersonalData]
    public string BurnerName { get; set; } = string.Empty;

    /// <summary>Legal first name (visible to member and board only).</summary>
    [PersonalData]
    public string FirstName { get; set; } = string.Empty;

    /// <summary>Legal last name (visible to member and board only).</summary>
    [PersonalData]
    public string LastName { get; set; } = string.Empty;

    [PersonalData]
    public string? City { get; set; }

    /// <summary>ISO 3166-1 alpha-2.</summary>
    [PersonalData]
    public string? CountryCode { get; set; }

    [PersonalData]
    public double? Latitude { get; set; }

    [PersonalData]
    public double? Longitude { get; set; }

    [PersonalData]
    public string? PlaceId { get; set; }

    [PersonalData]
    public string? Bio { get; set; }

    [PersonalData]
    public string? Pronouns { get; set; }

    [PersonalData]
    public LocalDate? DateOfBirth { get; set; }

    [PersonalData]
    public string? EmergencyContactName { get; set; }

    [PersonalData]
    public string? EmergencyContactPhone { get; set; }

    [PersonalData]
    public string? EmergencyContactRelationship { get; set; }

    // Dietary + medical — person-level attributes (moved off VolunteerEventProfile;
    // see docs/superpowers/specs/2026-05-25-dietary-medical-to-profile-design.md).

    /// <summary>Dietary preference (e.g., "Vegan", "Vegetarian", "Omnivore", "Pescatarian").</summary>
    [PersonalData]
    public string? DietaryPreference { get; set; }

    /// <summary>Food allergies.</summary>
    [PersonalData]
    public List<string> Allergies { get; set; } = [];

    /// <summary>Food intolerances.</summary>
    [PersonalData]
    public List<string> Intolerances { get; set; } = [];

    /// <summary>Free text specifying "Other" allergy when "Other" is selected.</summary>
    [PersonalData]
    public string? AllergyOtherText { get; set; }

    /// <summary>Free text specifying "Other" intolerance when "Other" is selected.</summary>
    [PersonalData]
    public string? IntoleranceOtherText { get; set; }

    /// <summary>
    /// Medical conditions — GDPR Art. 9 health data. Restricted visibility
    /// (owner / NoInfoAdmin / Admin only). Present on the cached UserInfo, so
    /// every render/serialize surface MUST gate it behind the MedicalDataViewer
    /// policy — never surface it without that check.
    /// </summary>
    [PersonalData]
    public string? MedicalConditions { get; set; }

    /// <summary>Obsolete — pictures live on the file share. DB column retained for prod-soak drop. See #702.</summary>
    [PersonalData]
    [Obsolete("Pictures live on the file share; this column is unused. The DB column stays until a follow-up PR after prod soak per memory/architecture/no-drops-until-prod-verified.md.", DiagnosticId = "HUM_PROFILE_PICTUREDATA", UrlFormat = "https://github.com/nobodies-collective/Humans/issues/702")]
    [Architecture.ExpiresOn("2026-06-01", reason: "Issue #702 — DB→FS migration complete (PR #576); column reserved for prod-soak drop.")]
    public byte[]? ProfilePictureData { get; set; }

    /// <summary>Doubles as the "has picture?" predicate; supplies the file extension.</summary>
    public string? ProfilePictureContentType { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    /// <summary>Administrative notes (not visible to member).</summary>
    public string? AdminNotes { get; set; }

    /// <summary>How the member would like to contribute — publicly visible.</summary>
    [PersonalData]
    public string? ContributionInterests { get; set; }

    /// <summary>Notes from member to Board (visible to self and board only).</summary>
    [PersonalData]
    public string? BoardNotes { get; set; }

    [PersonalData]
    public string? Iban { get; set; }

    /// <summary>Superseded by <see cref="State"/>; DB column retained for prod-soak drop. See #635.</summary>
    [Obsolete("Use Profile.State (ProfileState.Suspended) for new writes. The DB column stays until a follow-up PR after prod soak.", DiagnosticId = "HUM_PROFILE_ISSUSPENDED", UrlFormat = "https://github.com/nobodies-collective/Humans/issues/635")]
    public bool IsSuspended { get; set; }

    /// <summary>Lifecycle state — Stub/Active/Suspended. Nullable during lazy backfill.</summary>
    public ProfileState? State { get; set; }

    /// <summary>Auto-set when consent check clears.</summary>
    public bool IsApproved { get; set; }

    /// <summary>Default Volunteer; updated to Colaborador/Asociado on approved tier application.</summary>
    public MembershipTier MembershipTier { get; set; }

    /// <summary>Null until all required consents are signed; cleared triggers auto-approve.</summary>
    public ConsentCheckStatus? ConsentCheckStatus { get; set; }

    public Instant? ConsentCheckAt { get; set; }

    public Guid? ConsentCheckedByUserId { get; set; }

    public string? ConsentCheckNotes { get; set; }

    public string? RejectionReason { get; set; }

    public Instant? RejectedAt { get; set; }

    public Guid? RejectedByUserId { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();

    /// <summary>BurnerName > FirstName > "there".</summary>
    public string EmailGreetingName =>
        !string.IsNullOrWhiteSpace(BurnerName) ? BurnerName :
        !string.IsNullOrWhiteSpace(FirstName) ? FirstName : "there";

    public bool HasCustomProfilePicture => ProfilePictureContentType is not null;

    public ICollection<ContactField> ContactFields { get; } = new List<ContactField>();

    /// <summary>When true, Burner CV entries are not required.</summary>
    public bool NoPriorBurnExperience { get; set; }

    public ICollection<VolunteerHistoryEntry> VolunteerHistory { get; } = new List<VolunteerHistoryEntry>();

    public ICollection<ProfileLanguage> Languages { get; } = new List<ProfileLanguage>();
}

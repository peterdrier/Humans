using Microsoft.AspNetCore.Identity;
using NodaTime;

using Humans.Base.Attributes;
using Humans.Users.Contracts;

namespace Humans.Users.Domain;

internal sealed class Profile
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    [PersonalData]
    public string BurnerName { get; set; } = string.Empty;

    /// <summary>Visible to the member and the Board only.</summary>
    [PersonalData]
    public string FirstName { get; set; } = string.Empty;

    /// <summary>Visible to the member and the Board only.</summary>
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
    [MarkdownContent]
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

    // Legacy food columns: Cantina owns the answers. Read only by Cantina's fallback and copy;
    // still exported and erased here until the retirement PR drops them.

    /// <summary>Dietary preference (e.g., "Vegan", "Vegetarian", "Omnivore", "Pescatarian").</summary>
    [PersonalData]
    public string? DietaryPreference { get; set; }

    [PersonalData]
    public List<string> Allergies { get; set; } = [];

    [PersonalData]
    public List<string> Intolerances { get; set; } = [];

    /// <summary>Free text specifying "Other" allergy when "Other" is selected.</summary>
    [PersonalData]
    public string? AllergyOtherText { get; set; }

    /// <summary>Free text specifying "Other" intolerance when "Other" is selected.</summary>
    [PersonalData]
    public string? IntoleranceOtherText { get; set; }

    /// <summary>
    /// GDPR Art. 9 health data, present on the cached UserInfo: every render or serialize surface
    /// MUST gate it behind the MedicalDataViewer policy.
    /// </summary>
    [PersonalData]
    public string? MedicalConditions { get; set; }

    /// <summary>Doubles as the "has picture?" predicate; supplies the file extension.</summary>
    public string? ProfilePictureContentType { get; set; }

    /// <summary>
    /// Versioned <c>IFileStorage</c> key of the current picture. Null while the picture still lives at the
    /// legacy deterministic key (profile id + extension); readers fall back to it.
    /// </summary>
    public string? ProfilePictureStorageKey { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    /// <summary>Not visible to the member.</summary>
    public string? AdminNotes { get; set; }

    /// <summary>Publicly visible.</summary>
    [PersonalData]
    public string? ContributionInterests { get; set; }

    /// <summary>Notes to the Board, visible to self and the Board only.</summary>
    [PersonalData]
    public string? BoardNotes { get; set; }

    [PersonalData]
    public string? Iban { get; set; }

    /// <summary>
    /// Frozen legacy column, like the review columns below: Consent's <c>consent_reviews</c> is the
    /// live review. Kept until the <c>consent.profile-review</c> copy is done in production;
    /// <see cref="RejectedAt"/> still feeds the state classifier's legacy arm until then.
    /// </summary>
    public bool IsApproved { get; set; }

    /// <summary>
    /// Frozen legacy column: Governance's <c>governance_membership_tiers</c> is the live tier. Kept
    /// until the <c>governance.profile-tier</c> copy is done in production.
    /// </summary>
    public LegacyMembershipTier MembershipTier { get; set; }

    /// <summary>Frozen legacy column (see <see cref="IsApproved"/>).</summary>
    public LegacyConsentCheckStatus? ConsentCheckStatus { get; set; }

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

using Microsoft.AspNetCore.Identity;
using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// Member profile containing personal details.
/// MembershipStatus is computed from RoleAssignments and ConsentRecords.
/// </summary>
public class Profile
{
    /// <summary>
    /// Unique identifier for the profile.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Foreign key to the user.
    /// </summary>
    public Guid UserId { get; init; }

    /// <summary>
    /// The name the member goes by (e.g., burner name, nickname).
    /// This is the primary display name visible to everyone.
    /// </summary>
    [PersonalData]
    public string BurnerName { get; set; } = string.Empty;

    /// <summary>
    /// Member's legal first name (for official documents).
    /// Only visible to the member and board members.
    /// </summary>
    [PersonalData]
    public string FirstName { get; set; } = string.Empty;

    /// <summary>
    /// Member's legal last name (for official documents).
    /// Only visible to the member and board members.
    /// </summary>
    [PersonalData]
    public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// Member's city.
    /// </summary>
    [PersonalData]
    public string? City { get; set; }

    /// <summary>
    /// Member's country code (ISO 3166-1 alpha-2).
    /// </summary>
    [PersonalData]
    public string? CountryCode { get; set; }

    /// <summary>
    /// Latitude coordinate for the member's location.
    /// </summary>
    [PersonalData]
    public double? Latitude { get; set; }

    /// <summary>
    /// Longitude coordinate for the member's location.
    /// </summary>
    [PersonalData]
    public double? Longitude { get; set; }

    /// <summary>
    /// Google Places ID for future reference.
    /// </summary>
    [PersonalData]
    public string? PlaceId { get; set; }

    /// <summary>
    /// Optional biography or personal statement.
    /// </summary>
    [PersonalData]
    public string? Bio { get; set; }

    /// <summary>
    /// Member's pronouns (e.g., "they/them", "she/her").
    /// </summary>
    [PersonalData]
    public string? Pronouns { get; set; }

    /// <summary>
    /// Member's date of birth.
    /// </summary>
    [PersonalData]
    public LocalDate? DateOfBirth { get; set; }

    /// <summary>
    /// Emergency contact person's name (next of kin, partner, etc.).
    /// </summary>
    [PersonalData]
    public string? EmergencyContactName { get; set; }

    /// <summary>
    /// Emergency contact person's phone number.
    /// </summary>
    [PersonalData]
    public string? EmergencyContactPhone { get; set; }

    /// <summary>
    /// Relationship to the emergency contact (e.g., "Partner", "Parent").
    /// </summary>
    [PersonalData]
    public string? EmergencyContactRelationship { get; set; }

    /// <summary>
    /// Custom profile picture data (resized to 256x256, max 2MB).
    /// Stored in database given small scale (~500 users).
    /// </summary>
    [PersonalData]
    public byte[]? ProfilePictureData { get; set; }

    /// <summary>
    /// MIME content type of the custom profile picture (e.g., "image/jpeg").
    /// </summary>
    public string? ProfilePictureContentType { get; set; }

    /// <summary>
    /// When the profile was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When the profile was last updated.
    /// </summary>
    public Instant UpdatedAt { get; set; }

    /// <summary>
    /// Administrative notes (not visible to member).
    /// </summary>
    public string? AdminNotes { get; set; }

    /// <summary>
    /// How the member would like to contribute (skills, interests, availability).
    /// Publicly visible on the profile.
    /// </summary>
    [PersonalData]
    public string? ContributionInterests { get; set; }

    /// <summary>
    /// Notes from the member intended for the Board (visible to self and board only).
    /// </summary>
    [PersonalData]
    public string? BoardNotes { get; set; }

    [PersonalData]
    public string? Iban { get; set; }

    /// <summary>
    /// Whether the member has been manually suspended.
    /// </summary>
    /// <remarks>
    /// Issue #635 (§15i): superseded by <see cref="State"/>. New write paths set
    /// <c>State = ProfileState.Suspended</c>; reads should consult
    /// <see cref="State"/> when present (it is the canonical lifecycle marker).
    /// The underlying DB column stays per
    /// <c>memory/architecture/no-drops-until-prod-verified.md</c> until a
    /// follow-up PR drops it after prod soak.
    /// </remarks>
    [Obsolete("Use Profile.State (ProfileState.Suspended) for new writes. The DB column stays until a follow-up PR after prod soak.", DiagnosticId = "HUM_PROFILE_ISSUSPENDED", UrlFormat = "https://github.com/nobodies-collective/Humans/issues/635")]
    public bool IsSuspended { get; set; }

    /// <summary>
    /// Lifecycle state — Stub / Active / Suspended. Issue #635 (§15i): nullable
    /// while existing rows are lazily populated by
    /// <c>CachingProfileService</c>. New rows are created with an explicit
    /// <see cref="ProfileState.Stub"/> via the Stub Profile invariant.
    /// </summary>
    public ProfileState? State { get; set; }

    /// <summary>
    /// True when <see cref="BurnerName"/>, <see cref="FirstName"/>, and
    /// <see cref="LastName"/> are all populated (non-whitespace). The single
    /// canonical predicate for Stub→Active eligibility — used by
    /// <c>ProfileService.SaveProfileAsync</c>, <c>ProfileService.SetSuspendedAsync</c>,
    /// and <c>CachingProfileService.ComputeProfileState</c> so the rule cannot
    /// drift between write paths and lazy-compute paths.
    /// </summary>
    public bool HasRequiredIdentityFields() =>
        !string.IsNullOrWhiteSpace(BurnerName)
        && !string.IsNullOrWhiteSpace(FirstName)
        && !string.IsNullOrWhiteSpace(LastName);

    /// <summary>
    /// Whether the member has been approved for volunteer enrollment.
    /// Set automatically when consent check is cleared. New profiles default to false.
    /// </summary>
    public bool IsApproved { get; set; }

    /// <summary>
    /// The member's current membership tier. Tracked on Profile, not as a RoleAssignment.
    /// Default is Volunteer; updated to Colaborador/Asociado when a tier Application is approved.
    /// </summary>
    public MembershipTier MembershipTier { get; set; }

    /// <summary>
    /// Status of the consent check performed by a Consent Coordinator.
    /// Null until all required consents are signed (then auto-set to Pending).
    /// Cleared triggers auto-approve as Volunteer. Independent of tier applications.
    /// </summary>
    public ConsentCheckStatus? ConsentCheckStatus { get; set; }

    /// <summary>
    /// When the consent check was performed (cleared or flagged).
    /// </summary>
    public Instant? ConsentCheckAt { get; set; }

    /// <summary>
    /// ID of the Consent Coordinator who performed the consent check.
    /// </summary>
    public Guid? ConsentCheckedByUserId { get; set; }

    /// <summary>
    /// Notes from the Consent Coordinator (especially when flagging).
    /// </summary>
    public string? ConsentCheckNotes { get; set; }

    /// <summary>
    /// Reason for rejection (when an Admin rejects a flagged consent check).
    /// </summary>
    public string? RejectionReason { get; set; }

    /// <summary>
    /// When the profile was rejected.
    /// </summary>
    public Instant? RejectedAt { get; set; }

    /// <summary>
    /// ID of the Admin who rejected the profile.
    /// </summary>
    public Guid? RejectedByUserId { get; set; }

    /// <summary>
    /// Gets the full name of the member.
    /// </summary>
    public string FullName => $"{FirstName} {LastName}".Trim();

    /// <summary>
    /// Best available name for email greetings: BurnerName > FirstName > "there".
    /// </summary>
    public string EmailGreetingName =>
        !string.IsNullOrWhiteSpace(BurnerName) ? BurnerName :
        !string.IsNullOrWhiteSpace(FirstName) ? FirstName : "there";

    /// <summary>
    /// Whether this profile has a custom uploaded profile picture.
    /// </summary>
    public bool HasCustomProfilePicture => ProfilePictureData is not null && ProfilePictureData.Length > 0;

    /// <summary>
    /// Contact fields with visibility controls.
    /// </summary>
    public ICollection<ContactField> ContactFields { get; } = new List<ContactField>();

    /// <summary>
    /// Whether the member has declared they have no prior burn experience.
    /// When true, Burner CV entries are not required.
    /// </summary>
    public bool NoPriorBurnExperience { get; set; }

    /// <summary>
    /// Volunteer history entries documenting involvement in events, roles, and camps.
    /// </summary>
    public ICollection<VolunteerHistoryEntry> VolunteerHistory { get; } = new List<VolunteerHistoryEntry>();

    /// <summary>
    /// Languages spoken by the member with proficiency levels.
    /// </summary>
    public ICollection<ProfileLanguage> Languages { get; } = new List<ProfileLanguage>();
}

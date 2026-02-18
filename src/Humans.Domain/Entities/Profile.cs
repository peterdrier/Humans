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
    /// Navigation property to the user.
    /// </summary>
    public User User { get; set; } = null!;

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

    /// <summary>
    /// Whether the member has been manually suspended.
    /// </summary>
    public bool IsSuspended { get; set; }

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
    /// Computes the current membership status based on role assignments and consent records.
    /// </summary>
    /// <param name="currentRoleAssignments">Active role assignments for this user.</param>
    /// <param name="requiredDocumentVersionIds">IDs of document versions that require consent.</param>
    /// <param name="consentedDocumentVersionIds">IDs of document versions the user has consented to.</param>
    /// <returns>The computed membership status.</returns>
    public MembershipStatus ComputeMembershipStatus(
        IEnumerable<RoleAssignment> currentRoleAssignments,
        IEnumerable<Guid> requiredDocumentVersionIds,
        IEnumerable<Guid> consentedDocumentVersionIds)
    {
        if (IsSuspended)
        {
            return MembershipStatus.Suspended;
        }

        if (!IsApproved)
        {
            return MembershipStatus.Pending;
        }

        var activeRoles = currentRoleAssignments
            .Where(ra => ra.IsActive(SystemClock.Instance.GetCurrentInstant()))
            .ToList();

        if (activeRoles.Count == 0)
        {
            return MembershipStatus.None;
        }

        var requiredIds = requiredDocumentVersionIds.ToHashSet();
        var consentedIds = consentedDocumentVersionIds.ToHashSet();

        // Check if all required documents have valid consent
        var missingConsent = requiredIds.Except(consentedIds).Any();

        if (missingConsent)
        {
            return MembershipStatus.Inactive;
        }

        return MembershipStatus.Active;
    }

    /// <summary>
    /// Gets the full name of the member.
    /// </summary>
    public string FullName => $"{FirstName} {LastName}".Trim();

    /// <summary>
    /// Whether this profile has a custom uploaded profile picture.
    /// </summary>
    public bool HasCustomProfilePicture => ProfilePictureData != null && ProfilePictureData.Length > 0;

    /// <summary>
    /// Contact fields with visibility controls.
    /// </summary>
    public ICollection<ContactField> ContactFields { get; } = new List<ContactField>();

    /// <summary>
    /// Volunteer history entries documenting involvement in events, roles, and camps.
    /// </summary>
    public ICollection<VolunteerHistoryEntry> VolunteerHistory { get; } = new List<VolunteerHistoryEntry>();
}

using NodaTime;
using Profiles.Domain.Enums;

namespace Profiles.Domain.Entities;

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
    public string BurnerName { get; set; } = string.Empty;

    /// <summary>
    /// Member's legal first name (for official documents).
    /// Only visible to the member and board members.
    /// </summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>
    /// Member's legal last name (for official documents).
    /// Only visible to the member and board members.
    /// </summary>
    public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// Phone country code (e.g., "+34" for Spain, "+1" for US).
    /// </summary>
    public string? PhoneCountryCode { get; set; }

    /// <summary>
    /// Member's phone number (without country code).
    /// </summary>
    public string? PhoneNumber { get; set; }

    /// <summary>
    /// Member's city.
    /// </summary>
    public string? City { get; set; }

    /// <summary>
    /// Member's country code (ISO 3166-1 alpha-2).
    /// </summary>
    public string? CountryCode { get; set; }

    /// <summary>
    /// Latitude coordinate for the member's location.
    /// </summary>
    public double? Latitude { get; set; }

    /// <summary>
    /// Longitude coordinate for the member's location.
    /// </summary>
    public double? Longitude { get; set; }

    /// <summary>
    /// Google Places ID for future reference.
    /// </summary>
    public string? PlaceId { get; set; }

    /// <summary>
    /// Optional biography or personal statement.
    /// </summary>
    public string? Bio { get; set; }

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
    /// Whether the member has been manually suspended.
    /// </summary>
    public bool IsSuspended { get; set; }

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
    /// Contact fields with visibility controls.
    /// </summary>
    public ICollection<ContactField> ContactFields { get; } = new List<ContactField>();

    /// <summary>
    /// Volunteer history entries documenting involvement in events, roles, and camps.
    /// </summary>
    public ICollection<VolunteerHistoryEntry> VolunteerHistory { get; } = new List<VolunteerHistoryEntry>();
}

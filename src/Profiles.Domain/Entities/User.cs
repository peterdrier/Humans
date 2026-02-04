using Microsoft.AspNetCore.Identity;
using NodaTime;

namespace Profiles.Domain.Entities;

/// <summary>
/// Custom user entity extending ASP.NET Core Identity.
/// </summary>
public class User : IdentityUser<Guid>
{
    /// <summary>
    /// Display name for the user.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Preferred language code (e.g., "en", "es").
    /// Defaults to English.
    /// </summary>
    public string PreferredLanguage { get; set; } = "en";

    /// <summary>
    /// Google profile picture URL.
    /// </summary>
    public string? ProfilePictureUrl { get; set; }

    /// <summary>
    /// When the user account was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When the user last logged in.
    /// </summary>
    public Instant? LastLoginAt { get; set; }

    /// <summary>
    /// Navigation property to the member profile.
    /// </summary>
    public Profile? Profile { get; set; }

    /// <summary>
    /// Navigation property to role assignments.
    /// </summary>
    public ICollection<RoleAssignment> RoleAssignments { get; } = new List<RoleAssignment>();

    /// <summary>
    /// Navigation property to consent records.
    /// </summary>
    public ICollection<ConsentRecord> ConsentRecords { get; } = new List<ConsentRecord>();

    /// <summary>
    /// Navigation property to applications.
    /// </summary>
    public ICollection<Application> Applications { get; } = new List<Application>();

    /// <summary>
    /// Navigation property to team memberships.
    /// </summary>
    public ICollection<TeamMember> TeamMemberships { get; } = new List<TeamMember>();
}

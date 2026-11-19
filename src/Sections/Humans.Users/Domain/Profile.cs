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

    /// <summary>
    /// GDPR Art. 9 health data. Not on the cached UserInfo; <c>MedicalConditionsService</c> is its only reader,
    /// and anyone but the owner gets it through the audited reveal.
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

    public string FullName => $"{FirstName} {LastName}".Trim();

    public bool HasCustomProfilePicture => ProfilePictureContentType is not null;

    public ICollection<ContactField> ContactFields { get; } = new List<ContactField>();

    /// <summary>When true, Burner CV entries are not required.</summary>
    public bool NoPriorBurnExperience { get; set; }

    public ICollection<VolunteerHistoryEntry> VolunteerHistory { get; } = new List<VolunteerHistoryEntry>();

    public ICollection<ProfileLanguage> Languages { get; } = new List<ProfileLanguage>();
}

using System.ComponentModel.DataAnnotations;
using Profiles.Domain.Enums;

namespace Profiles.Web.Models;

public class ProfileViewModel
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }

    [Required]
    [StringLength(100)]
    [Display(Name = "Burner Name")]
    public string BurnerName { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    [Display(Name = "Legal First Name")]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    [Display(Name = "Legal Last Name")]
    public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// Whether the viewer can see legal name (own profile or board member).
    /// </summary>
    public bool CanViewLegalName { get; set; }

    [Display(Name = "Country Code")]
    [StringLength(5)]
    [RegularExpression(@"^\+\d{1,4}$", ErrorMessage = "Please enter a valid country code (e.g., +34, +1)")]
    public string? PhoneCountryCode { get; set; }

    [Display(Name = "Phone Number")]
    [StringLength(20)]
    [RegularExpression(@"^[\d\s\-]+$", ErrorMessage = "Please enter a valid phone number")]
    public string? PhoneNumber { get; set; }

    [StringLength(256)]
    public string? City { get; set; }

    [Display(Name = "Country")]
    [StringLength(2)]
    public string? CountryCode { get; set; }

    /// <summary>
    /// Latitude coordinate from Google Places.
    /// </summary>
    public double? Latitude { get; set; }

    /// <summary>
    /// Longitude coordinate from Google Places.
    /// </summary>
    public double? Longitude { get; set; }

    /// <summary>
    /// Google Places ID for future reference.
    /// </summary>
    [StringLength(512)]
    public string? PlaceId { get; set; }

    /// <summary>
    /// Display-friendly location string for the autocomplete input.
    /// </summary>
    public string? LocationDisplay => !string.IsNullOrEmpty(City) && !string.IsNullOrEmpty(CountryCode)
        ? $"{City}, {CountryCode}"
        : City ?? CountryCode;

    [StringLength(1000)]
    [DataType(DataType.MultilineText)]
    public string? Bio { get; set; }

    public string MembershipStatus { get; set; } = "None";
    public bool HasPendingConsents { get; set; }
    public int PendingConsentCount { get; set; }

    /// <summary>
    /// Gets the formatted phone number with country code.
    /// </summary>
    public string? FormattedPhoneNumber =>
        !string.IsNullOrEmpty(PhoneCountryCode) && !string.IsNullOrEmpty(PhoneNumber)
            ? $"{PhoneCountryCode} {PhoneNumber}"
            : PhoneNumber;

    /// <summary>
    /// Contact fields visible to the current viewer (for display).
    /// </summary>
    public IReadOnlyList<ContactFieldViewModel> ContactFields { get; set; } = [];

    /// <summary>
    /// Contact fields for editing (owner only).
    /// </summary>
    public List<ContactFieldEditViewModel> EditableContactFields { get; set; } = [];
}

/// <summary>
/// Contact field for display purposes.
/// </summary>
public class ContactFieldViewModel
{
    public Guid Id { get; set; }
    public ContactFieldType FieldType { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public ContactFieldVisibility Visibility { get; set; }

    /// <summary>
    /// Gets a Font Awesome icon class for this field type.
    /// </summary>
    public string IconClass => FieldType switch
    {
        ContactFieldType.Email => "fa-envelope",
        ContactFieldType.Phone => "fa-phone",
        ContactFieldType.Signal => "fa-comment-dots",
        ContactFieldType.Telegram => "fa-paper-plane",
        ContactFieldType.WhatsApp => "fa-whatsapp",
        _ => "fa-address-card"
    };

    /// <summary>
    /// Gets a visibility icon class.
    /// </summary>
    public string VisibilityIconClass => Visibility switch
    {
        ContactFieldVisibility.BoardOnly => "fa-lock",
        ContactFieldVisibility.LeadsAndBoard => "fa-user-shield",
        ContactFieldVisibility.MyTeams => "fa-users",
        ContactFieldVisibility.AllActiveProfiles => "fa-globe",
        _ => "fa-eye"
    };

    /// <summary>
    /// Gets a visibility tooltip.
    /// </summary>
    public string VisibilityTooltip => Visibility switch
    {
        ContactFieldVisibility.BoardOnly => "Visible to board members only",
        ContactFieldVisibility.LeadsAndBoard => "Visible to team leads and board",
        ContactFieldVisibility.MyTeams => "Visible to members of your teams",
        ContactFieldVisibility.AllActiveProfiles => "Visible to all active members",
        _ => "Visibility unknown"
    };
}

/// <summary>
/// Contact field for editing purposes.
/// </summary>
public class ContactFieldEditViewModel
{
    public Guid? Id { get; set; }

    [Required]
    public ContactFieldType FieldType { get; set; }

    [StringLength(100)]
    [Display(Name = "Custom Label")]
    public string? CustomLabel { get; set; }

    [Required]
    [StringLength(500)]
    public string Value { get; set; } = string.Empty;

    [Required]
    public ContactFieldVisibility Visibility { get; set; } = ContactFieldVisibility.AllActiveProfiles;

    public int DisplayOrder { get; set; }
}

using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// A contact field on a member's profile with visibility controls.
/// </summary>
public class ContactField
{
    /// <summary>
    /// Unique identifier for the contact field.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Foreign key to the profile.
    /// </summary>
    public Guid ProfileId { get; init; }

    /// <summary>
    /// Navigation property to the profile.
    /// </summary>
    public Profile Profile { get; set; } = null!;

    /// <summary>
    /// The type of contact field.
    /// </summary>
    public ContactFieldType FieldType { get; set; }

    /// <summary>
    /// Custom label for "Other" field type.
    /// </summary>
    public string? CustomLabel { get; set; }

    /// <summary>
    /// The contact value (e.g., email address, phone number, username).
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Who can see this contact field.
    /// </summary>
    public ContactFieldVisibility Visibility { get; set; }

    /// <summary>
    /// Display order for sorting fields.
    /// </summary>
    public int DisplayOrder { get; set; }

    /// <summary>
    /// When the contact field was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When the contact field was last updated.
    /// </summary>
    public Instant UpdatedAt { get; set; }

    /// <summary>
    /// Gets the display label for this field.
    /// </summary>
    public string DisplayLabel => FieldType == ContactFieldType.Other
        ? CustomLabel ?? "Other"
        : FieldType.ToString();
}

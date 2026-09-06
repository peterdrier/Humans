using NodaTime;

using Humans.Users.Contracts;

namespace Humans.Users.Domain;

/// <summary>
/// A contact field on a member's profile with visibility controls.
/// </summary>
internal sealed class ContactField
{
    public Guid Id { get; init; }

    public Guid ProfileId { get; init; }

    public Profile Profile { get; set; } = null!;

    public ContactFieldType FieldType { get; set; }

    /// <summary>
    /// Custom label for "Other" field type.
    /// </summary>
    public string? CustomLabel { get; set; }

    public string Value { get; set; } = string.Empty;

    public ContactFieldVisibility Visibility { get; set; }

    public int DisplayOrder { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    /// <summary>
    /// Gets the display label for this field.
    /// </summary>
    public string DisplayLabel => FieldType == ContactFieldType.Other
        ? CustomLabel ?? "Other"
        : FieldType.ToString();
}

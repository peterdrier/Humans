
namespace Humans.Users.Contracts;

public record ContactFieldDto(
    Guid Id,
    ContactFieldType FieldType,
    string Label,
    string Value,
    ContactFieldVisibility Visibility)
{
    public int DisplayOrder { get; init; }
}

public record ContactFieldEditDto(
    Guid? Id,
    ContactFieldType FieldType,
    string? CustomLabel,
    string Value,
    ContactFieldVisibility Visibility,
    int DisplayOrder);

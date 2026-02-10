using Humans.Domain.Enums;

namespace Humans.Application.DTOs;

/// <summary>
/// Contact field data for display purposes.
/// </summary>
public record ContactFieldDto(
    Guid Id,
    ContactFieldType FieldType,
    string Label,
    string Value,
    ContactFieldVisibility Visibility);

/// <summary>
/// Contact field data for editing purposes.
/// </summary>
public record ContactFieldEditDto(
    Guid? Id,
    ContactFieldType FieldType,
    string? CustomLabel,
    string Value,
    ContactFieldVisibility Visibility,
    int DisplayOrder);

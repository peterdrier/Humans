using Humans.Domain.Enums;

namespace Humans.Application.DTOs;

public record AdminContactRow(
    Guid UserId,
    string Email,
    string DisplayName,
    ContactSource? ContactSource,
    string? ExternalSourceId,
    DateTime CreatedAt,
    bool HasCommunicationPreferences);

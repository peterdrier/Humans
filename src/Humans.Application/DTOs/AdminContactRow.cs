using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.DTOs;

public record AdminContactRow(
    Guid UserId,
    string Email,
    string DisplayName,
    ContactSource? ContactSource,
    string? ExternalSourceId,
    Instant CreatedAt,
    bool HasCommunicationPreferences);

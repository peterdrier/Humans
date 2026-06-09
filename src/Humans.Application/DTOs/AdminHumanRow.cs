namespace Humans.Application.DTOs;

using Humans.Domain.Enums;

public record AdminHumanRow(
    Guid UserId,
    string Email,
    string DisplayName,
    string? ProfilePictureUrl,
    DateTime CreatedAt,
    DateTime? LastLoginAt,
    UserState State);

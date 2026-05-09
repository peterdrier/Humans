namespace Humans.Application.DTOs;

public record AdminHumanRow(
    Guid UserId,
    string Email,
    string BurnerName,
    string? ProfilePictureUrl,
    DateTime CreatedAt,
    DateTime? LastLoginAt,
    bool HasProfile,
    bool IsApproved,
    string MembershipStatus);

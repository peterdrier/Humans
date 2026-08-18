using Humans.Users.Contracts;
namespace Humans.Users.Models;

internal sealed record AdminHumanRow(
    Guid UserId,
    string Email,
    string DisplayName,
    string? ProfilePictureUrl,
    DateTime CreatedAt,
    DateTime? LastLoginAt,
    UserState State);

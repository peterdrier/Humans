namespace Humans.Application.DTOs;

public record BirthdayProfileInfo(
    Guid UserId,
    string DisplayName,
    string? ProfilePictureUrl,
    bool HasCustomPicture,
    Guid ProfileId,
    int Day,
    int Month);

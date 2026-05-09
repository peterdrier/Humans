namespace Humans.Application.DTOs;

public record BirthdayProfileInfo(
    Guid UserId,
    string BurnerName,
    string? ProfilePictureUrl,
    bool HasCustomPicture,
    Guid ProfileId,
    int Day,
    int Month);

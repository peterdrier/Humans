namespace Humans.Teams.Models;

internal sealed record BirthdayProfileInfo(
    Guid UserId,
    string DisplayName,
    string? ProfilePictureUrl,
    bool HasCustomPicture,
    Guid ProfileId,
    int Day,
    int Month);

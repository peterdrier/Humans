namespace Humans.Application.DTOs;

public record UserSearchResult(Guid UserId, string DisplayName, string Email);

public record HumanSearchResult(
    Guid UserId,
    string DisplayName,
    string? BurnerName,
    string? City,
    string? Bio,
    string? ContributionInterests,
    string? ProfilePictureUrl,
    bool HasCustomPicture,
    Guid ProfileId,
    long UpdatedAtTicks,
    string? MatchField,
    string? MatchSnippet);

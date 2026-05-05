namespace Humans.Application.DTOs;

public record HumanSearchResult(
    Guid UserId,
    string DisplayName,
    string? BurnerName,
    string? City,
    string? Bio,
    string? ContributionInterests,
    string? Pronouns,
    string? PrimaryEmail,
    string? ProfilePictureUrl,
    bool HasCustomPicture,
    Guid ProfileId,
    long UpdatedAtTicks,
    string? MatchField,
    string? MatchSnippet);

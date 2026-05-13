namespace Humans.Web.Models;

public record HumanLookupSearchResult(
    Guid UserId,
    string DisplayName,
    string? Detail = null,
    string? ProfilePictureUrl = null);

public record RoleAssignmentSearchResult(Guid Id, string DisplayName, string Email, bool OnTeam);

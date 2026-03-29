namespace Humans.Web.Models;

public record HumanLookupSearchResult(Guid UserId, string DisplayName, string? BurnerName);

public record ApprovedUserSearchResult(Guid UserId, string DisplayName, string Email);

public record RoleAssignmentSearchResult(Guid Id, string DisplayName, string Email, bool OnTeam);

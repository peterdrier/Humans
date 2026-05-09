namespace Humans.Web.Models;

public record HumanLookupSearchResult(Guid UserId, string BurnerName);

public record RoleAssignmentSearchResult(Guid Id, string BurnerName, string Email, bool OnTeam);

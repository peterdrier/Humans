namespace Humans.Web.Models;

public record RoleAssignmentSearchResult(Guid Id, string DisplayName, string Email, bool OnTeam);

public record BurnerNameCountResult(int Count);

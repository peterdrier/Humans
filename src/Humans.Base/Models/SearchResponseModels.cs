namespace Humans.Base.Models;

// JSON row shapes for the person-search endpoints. In Humans.Base rather than Shell because
// Humans.Teams' admin controller returns RoleAssignmentSearchResult and a section cannot
// reference Humans.Web; neither record names any section vocabulary, which is the test
// (design §15 step 6, Gate's HumanLookupSearchResult call).

public record RoleAssignmentSearchResult(Guid Id, string DisplayName, string Email, bool OnTeam);

public record BurnerNameCountResult(int Count);

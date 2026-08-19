using Microsoft.AspNetCore.Authorization;

namespace Humans.Users.Authorization;

/// <summary>
/// Requirement satisfied when the user has HumanAdmin role but NOT Admin or Board.
/// Used for the standalone "Humans" nav link that only appears when the user
/// has HumanAdmin without the broader Board/Admin access.
/// </summary>
internal sealed class HumanAdminOnlyRequirement : IAuthorizationRequirement;

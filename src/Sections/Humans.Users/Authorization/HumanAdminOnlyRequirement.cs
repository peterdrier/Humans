using Microsoft.AspNetCore.Authorization;

namespace Humans.Users.Authorization;

/// <summary>HumanAdmin without Admin or Board: drives the standalone "Humans" nav link.</summary>
internal sealed class HumanAdminOnlyRequirement : IAuthorizationRequirement;

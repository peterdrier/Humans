using System.Security.Claims;
using Humans.Application.Models;

namespace Humans.Application.Interfaces;

/// <summary>
/// Builds a <see cref="GuideRoleContext"/> for the current user: reads system roles
/// from claims and checks the database for any active team-coordinator assignment.
/// </summary>
public interface IGuideRoleResolver
{
    Task<GuideRoleContext> ResolveAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
}

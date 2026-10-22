using System.Security.Claims;

namespace Humans.Guide.Services;

internal interface IGuideRoleResolver
{
    Task<GuideRoleContext> ResolveAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
}

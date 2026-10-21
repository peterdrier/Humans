using Humans.Auth.Contracts;
using System.Security.Claims;

namespace Humans.Web.Authorization;

internal sealed class HttpCurrentUserContext(IHttpContextAccessor httpContextAccessor) : ICurrentUserContext
{
    public Guid? UserId
    {
        get
        {
            var user = httpContextAccessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true)
                return null;

            var rawUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(rawUserId, out var userId) ? userId : null;
        }
    }
}

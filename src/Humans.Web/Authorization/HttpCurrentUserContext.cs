using System.Security.Claims;
using Humans.Application.Interfaces.Auth;

namespace Humans.Web.Authorization;

internal sealed class HttpCurrentUserContext : ICurrentUserContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpCurrentUserContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? UserId
    {
        get
        {
#pragma warning disable CS0618 // False positive for obsolete domain nav scan; this is HttpContext.User.
            var user = _httpContextAccessor.HttpContext?.User;
#pragma warning restore CS0618
            if (user?.Identity?.IsAuthenticated != true)
                return null;

            var rawUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(rawUserId, out var userId) ? userId : null;
        }
    }
}

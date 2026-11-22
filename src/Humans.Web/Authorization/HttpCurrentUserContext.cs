using Humans.Base.Extensions;
using Humans.Auth.Contracts;

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

            return user.GetUserId();
        }
    }
}

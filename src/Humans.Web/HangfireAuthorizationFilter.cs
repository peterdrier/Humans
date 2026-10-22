using Hangfire.Dashboard;
using Humans.Base.Authorization;

namespace Humans.Web;

/// <summary>Hangfire dashboard: authenticated administrators only.</summary>
public class HangfireAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();

        return httpContext.User.Identity?.IsAuthenticated == true
            && RoleChecks.IsAdmin(httpContext.User);
    }
}

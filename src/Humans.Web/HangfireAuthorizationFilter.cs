using Hangfire.Dashboard;
using Humans.Base.Authorization;

namespace Humans.Web;

/// <summary>Hangfire dashboard: Admin and Board; <c>Program.cs</c> makes it read-only for everyone but Admin.</summary>
public class HangfireAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();

        return httpContext.User.Identity?.IsAuthenticated == true
            && RoleChecks.IsAdminOrBoard(httpContext.User);
    }
}

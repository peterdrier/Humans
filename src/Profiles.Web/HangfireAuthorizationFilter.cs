using Hangfire.Dashboard;

namespace Profiles.Web;

/// <summary>
/// Authorization filter for the Hangfire dashboard.
/// Only allows authenticated administrators.
/// </summary>
public class HangfireAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();

        // Only allow authenticated users with Admin role
        return httpContext.User.Identity?.IsAuthenticated == true
            && httpContext.User.IsInRole("Admin");
    }
}

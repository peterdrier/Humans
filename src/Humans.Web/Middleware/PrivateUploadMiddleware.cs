using System.Security.Claims;
using Humans.Base.Authorization;
using Humans.Issues.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Middleware;

/// <summary>Applies access gates before the static-file middleware can serve private uploads.</summary>
internal sealed class PrivateUploadMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = NormalizePath(context.Request.Path);
        if (path.StartsWithSegments("/uploads/profile-pictures", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var feedback = path.StartsWithSegments("/uploads/feedback", StringComparison.OrdinalIgnoreCase);
        var issue = path.StartsWithSegments("/uploads/issues", StringComparison.OrdinalIgnoreCase, out var remaining);
        if (feedback || issue)
        {
            context.Response.Headers.CacheControl = "private, no-store";
            // Static files run before the normal authentication middleware.
            var authentication = await context.AuthenticateAsync();
            var allowed = false;
            if (authentication.Succeeded && authentication.Principal is { } principal)
            {
                context.User = principal;
                if (feedback)
                    allowed = (await context.RequestServices.GetRequiredService<IAuthorizationService>()
                        .AuthorizeAsync(principal, context, PolicyNames.AdminOnly)).Succeeded;
                else if (Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
                    && Guid.TryParse(remaining.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), out var issueId))
                {
                    var viewer = new IssueViewer(userId, principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList());
                    var detail = await context.RequestServices.GetRequiredService<IIssueTriage>()
                        .GetIssueByIdAsync(issueId, viewer, context.RequestAborted);
                    allowed = detail is not null && string.Equals(detail.ScreenshotStoragePath, path.Value![1..], StringComparison.Ordinal);
                }
            }

            if (!allowed)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }

        await next(context);
    }

    private static PathString NormalizePath(PathString path)
    {
        var segments = new List<string>();
        foreach (var segment in (path.Value ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(segment, ".", StringComparison.Ordinal)) continue;
            if (string.Equals(segment, "..", StringComparison.Ordinal))
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
            }
            else segments.Add(segment);
        }
        return new PathString("/" + string.Join('/', segments));
    }
}

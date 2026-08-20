using System.Text;
using Microsoft.AspNetCore.Http.Extensions;

namespace Humans.Web.Middleware;

/// <summary>
/// Dev/QA-only response scanner: an unresolved <c>&lt;vc:...&gt;</c> element ships to the
/// browser as literal markup with a green build, no exception, and no log line
/// (nobodies-collective/Humans#1055) — Razor only generates a view-component tag helper call
/// when the component type is public AND the view's folder imports the assembly via
/// <c>@addTagHelper</c>. <see cref="Hosting.SectionViewComponentFeatureProvider"/> relaxes
/// MVC's runtime lookup for section assemblies, but nothing relaxes Razor's compile-time tag
/// helper generation, so a non-public component (or a missing directive) silently never gets a
/// call site. This middleware buffers HTML responses and logs a warning naming the page and the
/// offending element when one survives into the rendered output, turning that silent failure
/// into a log line on every preview deploy.
/// </summary>
/// <remarks>
/// Registered only for Development/Staging(QA) in <c>Program.cs</c> — never Production or
/// Testing: it is a dev-loop guard, not a runtime safety net, and buffering a response body is
/// not free. Buffering is further scoped to requests a real browser navigation would send — GET
/// with an <c>Accept</c> header preferring <c>text/html</c> — so downloads, exports, JSON APIs
/// and images are never buffered; the response's actual <c>Content-Type</c> is checked again
/// after the pipeline runs before anything is scanned.
/// </remarks>
public sealed class ViewComponentTagSurvivalMiddleware(
    RequestDelegate next,
    ILogger<ViewComponentTagSurvivalMiddleware> logger)
{
    private const string TagPrefix = "<vc:";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!LooksLikeHtmlNavigation(context.Request))
        {
            await next(context);
            return;
        }

        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        if (context.Response.ContentType is { } contentType
            && contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            ReportSurvivors(context, Encoding.UTF8.GetString(buffer.ToArray()), logger);
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody);
    }

    /// <summary>
    /// A cheap pre-filter, checked before anything is buffered: only requests a real browser
    /// navigation sends carry this Accept header shape, so AJAX/API calls, downloads, exports
    /// and image requests never pay for the buffer.
    /// </summary>
    private static bool LooksLikeHtmlNavigation(HttpRequest request) =>
        HttpMethods.IsGet(request.Method)
        && request.Headers.Accept.Any(value =>
            value is not null && value.Contains("text/html", StringComparison.OrdinalIgnoreCase));

    private static void ReportSurvivors(HttpContext context, string html, ILogger logger)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = html.IndexOf(TagPrefix, StringComparison.Ordinal);

        while (index >= 0)
        {
            var close = html.IndexOf('>', index);
            var length = close > index ? close - index + 1 : Math.Min(80, html.Length - index);
            var element = html.Substring(index, length);

            if (seen.Add(element))
            {
                logger.LogWarning(
                    "Unresolved <vc:> element survived into rendered HTML — it never got a tag " +
                    "helper call, so the browser received it as literal markup (see " +
                    "nobodies-collective/Humans#1055). Path={Path}, Element={Element}",
                    context.Request.GetEncodedPathAndQuery(),
                    element);
            }

            index = html.IndexOf(TagPrefix, index + TagPrefix.Length, StringComparison.Ordinal);
        }
    }
}

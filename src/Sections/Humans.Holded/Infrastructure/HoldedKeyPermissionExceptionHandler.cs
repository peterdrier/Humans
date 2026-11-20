using Humans.Base.Constants;
using Humans.Holded.Contracts;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Humans.Holded.Infrastructure;

/// <summary>
/// A Holded call refused because the API key lacks a scope is a setup fix in Holded, not a crash: the page that made
/// the call is shown again with the message naming the missing scope, instead of the error page.
/// </summary>
internal sealed class HoldedKeyPermissionExceptionHandler(
    ITempDataDictionaryFactory tempDataFactory,
    ILogger<HoldedKeyPermissionExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not HoldedPermanentException { StatusCode: 403 } holded
            || holded.ResponseBody is not { } body
            || Services.HoldedTransport.MissingKeyPermission(body) is null
            || httpContext.Response.HasStarted)
            return false;

        logger.LogWarning("Holded refused a call on {Method} {Path}: {Message}",
            httpContext.Request.Method, httpContext.Request.Path, holded.Message);

        var back = LocalReferer(httpContext);
        if (back is not null)
        {
            var tempData = tempDataFactory.GetTempData(httpContext);
            tempData[TempDataKeys.ErrorMessage] = holded.Message;
            tempData.Save();
            httpContext.Response.Redirect(back);
            return true;
        }

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.ContentType = "text/plain; charset=utf-8";
        await httpContext.Response.WriteAsync(holded.Message, cancellationToken);
        return true;
    }

    /// <summary>Only a same-host page: the referer is client-sent, and redirecting off-site would be an open redirect.</summary>
    private static string? LocalReferer(HttpContext httpContext)
    {
        var referer = httpContext.Request.Headers.Referer.ToString();
        if (!Uri.TryCreate(referer, UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Host, httpContext.Request.Host.Host, StringComparison.OrdinalIgnoreCase)) return null;
        // A GET that failed would fail again on the same URL; only a form post goes back to its page.
        if (HttpMethods.IsGet(httpContext.Request.Method)
            && string.Equals(uri.PathAndQuery, httpContext.Request.Path + httpContext.Request.QueryString, StringComparison.Ordinal))
            return null;
        return uri.PathAndQuery;
    }
}

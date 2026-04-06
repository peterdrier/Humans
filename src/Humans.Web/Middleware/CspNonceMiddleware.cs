using System.Security.Cryptography;

namespace Humans.Web.Middleware;

public class CspNonceMiddleware
{
    private readonly RequestDelegate _next;

    public CspNonceMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var nonceBytes = RandomNumberGenerator.GetBytes(32);
        var nonce = Convert.ToBase64String(nonceBytes);
        context.Items["CspNonce"] = nonce;

        context.Response.Headers.Append("Content-Security-Policy",
            $"default-src 'self'; " +
            $"script-src 'self' 'nonce-{nonce}' https://cdn.jsdelivr.net https://unpkg.com https://maps.googleapis.com https://maps.gstatic.com; " +
            "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://cdnjs.cloudflare.com https://fonts.googleapis.com https://unpkg.com; " +
            "font-src 'self' https://fonts.gstatic.com https://cdn.jsdelivr.net https://cdnjs.cloudflare.com; " +
            "img-src 'self' https: data:; " +
            "connect-src 'self' https://cdn.jsdelivr.net https://unpkg.com https://maps.googleapis.com https://maps.gstatic.com https://places.googleapis.com https://server.arcgisonline.com wss: ws:; " +
            "worker-src blob:; " +
            "frame-ancestors 'none'");

        await _next(context);
    }
}

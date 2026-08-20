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
/// Testing: it is a dev-loop guard, not a runtime safety net, and holding a response body in
/// memory is not free. Non-GET requests skip the wrapper entirely; everything else is decided by
/// the response's own <c>Content-Type</c> at the first body write, so only HTML is ever held —
/// a CSV/XLSX/JSON download streams straight through even though a browser link click requests
/// it with the same <c>text/html</c> navigation <c>Accept</c> header a page does.
/// </remarks>
public sealed class ViewComponentTagSurvivalMiddleware(
    RequestDelegate next,
    ILogger<ViewComponentTagSurvivalMiddleware> logger)
{
    private const string TagPrefix = "<vc:";

    public async Task InvokeAsync(HttpContext context)
    {
        // A page is always a GET; nothing else can render a view component.
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await next(context);
            return;
        }

        var originalBody = context.Response.Body;
        await using var capture = new HtmlCaptureStream(originalBody, context.Response);
        context.Response.Body = capture;

        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        if (capture.Captured is not { } html)
            return;

        ReportSurvivors(context, Encoding.UTF8.GetString(html.ToArray()), logger);

        html.Position = 0;
        await html.CopyToAsync(originalBody);
    }

    /// <summary>
    /// Writes straight through to the real response body unless the response is HTML, in which
    /// case it collects the bytes for scanning. The decision is made from the response's own
    /// <c>Content-Type</c> at the first write — the request's <c>Accept</c> header cannot be
    /// used for it, because a browser sends the same <c>text/html</c> navigation header for a
    /// click on a CSV/XLSX download link as it does for a page (it does not learn the response
    /// type until the response arrives). Deciding on Content-Type keeps exports streaming.
    /// </summary>
    private sealed class HtmlCaptureStream(Stream inner, HttpResponse response) : Stream
    {
        private bool decided;

        public MemoryStream? Captured { get; private set; }

        private Stream Target()
        {
            if (!decided)
            {
                decided = true;
                if (response.ContentType is { } contentType
                    && contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    Captured = new MemoryStream();
                }
            }

            return Captured ?? inner;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Target().Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => Target().Write(buffer);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Target().WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            Target().WriteAsync(buffer, cancellationToken);

        public override void Flush() => Target().Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => Target().FlushAsync(cancellationToken);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Captured?.Dispose();

            base.Dispose(disposing);
        }
    }

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

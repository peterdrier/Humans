using System.Buffers.Text;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Options;

namespace Humans.Web.Infrastructure;

/// <summary>
/// Runs before MVC and validates the <see cref="CookieTempDataProvider"/> request cookie
/// before the framework gets to it. On .NET 9+, <c>CookieTempDataProvider.LoadTempData</c>
/// Base64Url-decodes the cookie and, on a malformed value, throws a context-free
/// <see cref="FormatException"/> with no request path, user-agent, or cookie length in the
/// log — see nobodies-collective/Humans#1038. This middleware performs the same decode
/// check first: a malformed cookie is logged once at Warning with full context (this is
/// deliberately still visible, not a silencing fix), stripped from the request so
/// <see cref="CookieTempDataProvider"/> never sees it, and deleted from the response so a
/// repeat request from the same client stops looping. A valid cookie — chunked or not — is
/// left completely untouched, so TempData keeps round-tripping normally.
/// </summary>
/// <remarks>
/// <see cref="CookieTempDataProvider"/> reassembles a large TempData value from chunk
/// cookies named <c>{cookieName}C1</c>, <c>{cookieName}C2</c>, ... via its internal
/// <c>Microsoft.AspNetCore.Internal.ChunkingCookieManager</c> (not a reusable public type,
/// so its chunk-marker format — <c>"chunks-" + count</c> prefixing the main cookie's raw
/// value, per the upstream <c>dotnet/aspnetcore</c> source — is reproduced here). Without
/// this reassembly, a healthy chunked cookie (e.g. the Events bulk-errors write) would
/// always fail a naive single-cookie decode check.
/// </remarks>
public sealed class TempDataCookieValidationMiddleware(
    RequestDelegate next,
    IOptions<CookieTempDataProviderOptions> tempDataOptions,
    ILogger<TempDataCookieValidationMiddleware> logger)
{
    private const string ChunkCountPrefix = "chunks-";
    private const string ChunkKeySuffix = "C";

    public async Task InvokeAsync(HttpContext context)
    {
        var cookieName = tempDataOptions.Value.Cookie.Name ?? CookieTempDataProvider.CookieName;
        var requestCookies = context.Request.Cookies;

        if (!requestCookies.TryGetValue(cookieName, out var rawValue) || string.IsNullOrEmpty(rawValue))
        {
            await next(context);
            return;
        }

        var hasChunkSiblings = requestCookies.ContainsKey(cookieName + ChunkKeySuffix + "1");
        var reassembled = Reassemble(requestCookies, cookieName, rawValue, out var relatedNames);

        if (reassembled is not null && Base64Url.IsValid(reassembled))
        {
            await next(context);
            return;
        }

        logger.LogWarning(
            "TempData cookie {CookieName} could not be decoded — removing it before " +
            "CookieTempDataProvider throws. Path={Path}, UserAgent={UserAgent}, " +
            "CookieLength={CookieLength}, HasChunkSiblings={HasChunkSiblings}",
            cookieName,
            context.Request.Path.Value,
            context.Request.Headers.UserAgent.ToString(),
            rawValue.Length,
            hasChunkSiblings);

        context.Request.Cookies = new FilteredRequestCookieCollection(requestCookies, relatedNames);

        var deleteOptions = tempDataOptions.Value.Cookie.Build(context);
        foreach (var name in relatedNames)
        {
            context.Response.Cookies.Delete(name, deleteOptions);
        }

        await next(context);
    }

    /// <summary>
    /// Reassembles the value <see cref="CookieTempDataProvider"/> would attempt to decode:
    /// the raw cookie value when it isn't chunked, or the concatenation of its <c>C1..CN</c>
    /// siblings when it is. Returns null when a chunked value is missing a sibling — the
    /// same incomplete state the framework's chunk manager falls back to (the raw
    /// "chunks-N" marker), which never decodes either way.
    /// </summary>
    private static string? Reassemble(
        IRequestCookieCollection requestCookies,
        string cookieName,
        string rawValue,
        out HashSet<string> relatedNames)
    {
        relatedNames = [cookieName];

        if (!rawValue.StartsWith(ChunkCountPrefix, StringComparison.Ordinal)
            || !int.TryParse(
                rawValue.AsSpan(ChunkCountPrefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var chunkCount)
            || chunkCount <= 0)
        {
            return rawValue;
        }

        var builder = new StringBuilder();
        for (var i = 1; i <= chunkCount; i++)
        {
            var chunkName = cookieName + ChunkKeySuffix + i.ToString(CultureInfo.InvariantCulture);
            if (!requestCookies.TryGetValue(chunkName, out var chunk) || string.IsNullOrEmpty(chunk))
            {
                return null;
            }

            relatedNames.Add(chunkName);
            builder.Append(chunk);
        }

        return builder.ToString();
    }

    /// <summary>
    /// An <see cref="IRequestCookieCollection"/> that hides a specific set of cookie names.
    /// Assigning an instance to <c>HttpRequest.Cookies</c> is how a middleware removes a
    /// cookie from the request in place — there is no public mutable implementation to
    /// reuse (the framework's own is internal), so this wraps the original collection.
    /// </summary>
    private sealed class FilteredRequestCookieCollection(
        IRequestCookieCollection inner,
        HashSet<string> excluded) : IRequestCookieCollection
    {
        public int Count => inner.Count - excluded.Count(inner.ContainsKey);

        public ICollection<string> Keys => inner.Keys.Where(k => !excluded.Contains(k)).ToArray();

        public string? this[string key] => excluded.Contains(key) ? null : inner[key];

        public bool ContainsKey(string key) => !excluded.Contains(key) && inner.ContainsKey(key);

        public bool TryGetValue(string key, [NotNullWhen(true)] out string? value)
        {
            if (excluded.Contains(key))
            {
                value = null;
                return false;
            }

            return inner.TryGetValue(key, out value);
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
            => inner.Where(kv => !excluded.Contains(kv.Key)).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

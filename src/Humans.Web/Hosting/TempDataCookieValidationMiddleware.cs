using System.Buffers.Text;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Options;

namespace Humans.Web.Hosting;

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
            // The marker cookie is the one CookieTempDataProvider looks up; siblings that outlive
            // it are weight it can never reassemble, re-sent on every request until they expire.
            // Swept without logging: with no main cookie there is no decode and so no #1038
            // FormatException, and this must not add noise to the stream that issue is read from.
            var orphans = ChunkNames(CollectChunkSiblings(requestCookies, cookieName));
            if (orphans.Count > 0)
            {
                RemoveFromRequestAndResponse(context, orphans);
            }

            await next(context);
            return;
        }

        var reassembled = Reassemble(requestCookies, cookieName, rawValue, out var relatedNames);

        // A chunked request is one whose main value carries the "chunks-N" marker — regardless of
        // which siblings survived, so losing C1 specifically still reports True. The second arm
        // catches the inverse: a main value corrupted past recognition, where no marker is left
        // to parse but siblings are still attached. relatedNames holds every sibling actually
        // present, so this covers a sibling at any index rather than only C1.
        var hasChunkSiblings = TryParseChunkCount(rawValue, out _) || relatedNames.Count > 1;

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

        RemoveFromRequestAndResponse(context, relatedNames);

        await next(context);
    }

    /// <summary>
    /// Hides <paramref name="names"/> from the downstream request and expires them on the
    /// response, so neither MVC nor the next request from this client sees them again.
    /// </summary>
    private void RemoveFromRequestAndResponse(HttpContext context, HashSet<string> names)
    {
        context.Request.Cookies = new FilteredRequestCookieCollection(context.Request.Cookies, names);

        var deleteOptions = tempDataOptions.Value.Cookie.Build(context);
        foreach (var name in names)
        {
            context.Response.Cookies.Delete(name, deleteOptions);
        }
    }

    /// <summary>
    /// The names to hide and expire, compared the way <see cref="IRequestCookieCollection"/>
    /// compares them so a lookup under the configured casing still finds a hidden cookie the
    /// client sent under another — while each stored name stays exactly as it arrived.
    /// </summary>
    private static HashSet<string> ChunkNames(Dictionary<int, (string Name, string Value)> siblings)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sibling in siblings.Values)
        {
            names.Add(sibling.Name);
        }

        return names;
    }

    /// <summary>
    /// The main cookie's name as the client sent it. The request collection resolves names
    /// case-insensitively, so a differently-cased cookie is found through the configured name
    /// — but a browser matches a Set-Cookie delete case-sensitively, so expiring the configured
    /// casing would leave the real cookie in place and re-log this warning on every request.
    /// </summary>
    private static string ResolveSentName(IRequestCookieCollection requestCookies, string cookieName)
    {
        foreach (var (name, _) in requestCookies)
        {
            if (string.Equals(name, cookieName, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return cookieName;
    }

    /// <summary>
    /// Parses the chunk marker the framework's chunk manager writes as the main cookie's raw
    /// value — <c>"chunks-" + count</c> — returning false for an unchunked or malformed value.
    /// </summary>
    private static bool TryParseChunkCount(string rawValue, out int chunkCount)
    {
        chunkCount = 0;
        return rawValue.StartsWith(ChunkCountPrefix, StringComparison.Ordinal)
            && int.TryParse(
                rawValue.AsSpan(ChunkCountPrefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out chunkCount)
            && chunkCount > 0;
    }

    /// <summary>
    /// Every <c>{cookieName}C{n}</c> sibling actually present on the request, keyed by index.
    /// </summary>
    /// <remarks>
    /// Found by scanning the request's own cookies rather than by generating <c>C1..CN</c> names
    /// from the main value's chunk marker. The marker is client-supplied: an anonymous request
    /// can send <c>chunks-2147483647</c>, and this middleware runs on every dynamic request, so
    /// letting that count drive a loop is a cheap CPU-denial-of-service vector. Scanning bounds
    /// the work by the number of cookies the client actually sent, and as a side benefit finds
    /// siblings even when the main value is corrupted past having a parseable marker at all.
    /// </remarks>
    private static Dictionary<int, (string Name, string Value)> CollectChunkSiblings(
        IRequestCookieCollection requestCookies,
        string cookieName)
    {
        var prefix = cookieName + ChunkKeySuffix;
        var siblings = new Dictionary<int, (string Name, string Value)>();

        foreach (var (name, value) in requestCookies)
        {
            // Matched case-insensitively because that is how the request collection this
            // mirrors resolves a name — but the name kept is the one the client actually
            // sent, since that is the only one a Set-Cookie delete will match.
            if (name.Length <= prefix.Length
                || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(value))
            {
                continue;
            }

            // The suffix must round-trip: the framework looks up the exact name it wrote, so
            // "C01" is not its C1. Treating it as index 1 would let this middleware reassemble a
            // valid-looking value and wave through a request the framework still fails on.
            var suffix = name.AsSpan(prefix.Length);
            if (int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index > 0
                && suffix.SequenceEqual(index.ToString(CultureInfo.InvariantCulture)))
            {
                siblings[index] = (name, value);
            }
        }

        return siblings;
    }

    /// <summary>
    /// Reassembles the value <see cref="CookieTempDataProvider"/> would attempt to decode:
    /// the raw cookie value when it isn't chunked, or the concatenation of its <c>C1..CN</c>
    /// siblings when it is. Returns null when a chunked value is missing a sibling — the
    /// same incomplete state the framework's chunk manager falls back to (the raw
    /// "chunks-N" marker), which never decodes either way.
    /// </summary>
    /// <remarks>
    /// <paramref name="relatedNames"/> collects every sibling present, unconditionally — before
    /// the marker is even parsed — because it drives both the request filter and the response
    /// deletes. Collecting only what reassembly happened to consume left orphans behind in two
    /// ways: a sibling after a gap in the range, and every sibling of a main value too corrupted
    /// to carry a marker. Either left the client re-sending kilobytes of dead cookies on every
    /// request for the rest of the session, which is the opposite of this middleware's contract.
    /// </remarks>
    private static string? Reassemble(
        IRequestCookieCollection requestCookies,
        string cookieName,
        string rawValue,
        out HashSet<string> relatedNames)
    {
        var siblings = CollectChunkSiblings(requestCookies, cookieName);

        relatedNames = ChunkNames(siblings);
        relatedNames.Add(ResolveSentName(requestCookies, cookieName));

        if (!TryParseChunkCount(rawValue, out var chunkCount))
        {
            return rawValue;
        }

        // The declared count can exceed what was sent — whether by cookie loss or by a hostile
        // marker. Either way it cannot reassemble, and bailing here keeps the loop below bounded
        // by the sibling count rather than by the client's number.
        if (chunkCount > siblings.Count)
        {
            return null;
        }

        var builder = new StringBuilder();
        for (var i = 1; i <= chunkCount; i++)
        {
            if (!siblings.TryGetValue(i, out var chunk))
            {
                return null;
            }

            builder.Append(chunk.Value);
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

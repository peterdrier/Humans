using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using Humans.Base.Extensions;

namespace Humans.Base.Http;

/// <summary>
/// The send path shared by integrations that call their vendor's HTTP API directly: destination check, credentials,
/// transport failures as temporary, status mapping and cursor paging. Each client keeps its own exception types
/// through <see cref="Transient"/> and <see cref="Permanent"/>.
/// </summary>
public abstract class ApiClientBase
{
    /// <summary>Sets the base address from <paramref name="baseUrl"/> when the client has none.</summary>
    /// <param name="http">The typed client.</param>
    /// <param name="baseUrl">Used when <paramref name="http"/> has no base address. A bad value does not stop DI;
    /// sends fail at <see cref="SendRawAsync"/> instead.</param>
    protected ApiClientBase(HttpClient http, string? baseUrl)
    {
        Http = http;
        if (http.BaseAddress is null && Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseAddress)
            && baseAddress.Scheme is "http" or "https")
            http.BaseAddress = baseAddress;
    }

    protected HttpClient Http { get; }

    /// <summary>The vendor's name at the start of every error message.</summary>
    protected abstract string ApiName { get; }

    protected bool HasBaseAddress => Http.BaseAddress is { Scheme: "http" or "https" };

    /// <summary>How much of a failed response's body a permanent error's message carries. None by default: a
    /// vendor can echo personal data back.</summary>
    protected virtual int ErrorBodyLength => 0;

    /// <summary>Worth retrying later: a send failure, a timeout, a 5xx or a 429.</summary>
    protected abstract Exception Transient(string message, Exception? inner = null);

    /// <summary>Retrying will not help.</summary>
    protected abstract Exception Permanent(string message, HttpStatusCode? status = null, string? body = null);

    /// <summary>Adds credentials. Called only for requests aimed under the base address, so a signed absolute URL
    /// never carries the key.</summary>
    protected virtual void Authorize(HttpRequestMessage request)
    {
    }

    /// <summary>Runs after every send that got a response, whatever its status.</summary>
    protected virtual void OnResponse(HttpRequestMessage request, HttpResponseMessage response, string caller)
    {
    }

    /// <summary>The error for a failed response.</summary>
    protected virtual Exception Failure(HttpResponseMessage response, string body, string caller)
    {
        var label = $"{ApiName} {caller}: {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)} {response.ReasonPhrase}";
        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            return Transient(label);
        return Permanent(
            ErrorBodyLength > 0 ? $"{label}: {body.Truncate(ErrorBodyLength)}" : label, response.StatusCode, body);
    }

    /// <summary>Sends and returns the response whatever its status; the caller disposes it.</summary>
    protected async Task<HttpResponseMessage> SendRawAsync(
        HttpRequestMessage request, CancellationToken ct, [CallerMemberName] string caller = "")
    {
        var destination = request.RequestUri is { IsAbsoluteUri: true } absolute ? absolute : Http.BaseAddress;
        if (destination is null || destination.Scheme is not ("http" or "https"))
            throw Permanent($"{ApiName}:BaseUrl must be an absolute HTTP(S) URL.");

        if (request.RequestUri is not { IsAbsoluteUri: true } || Http.BaseAddress?.IsBaseOf(request.RequestUri) == true)
            Authorize(request);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw Transient($"{ApiName} {caller}: send failed.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw Transient($"{ApiName} {caller}: timed out.", ex);
        }

        OnResponse(request, response, caller);
        return response;
    }

    /// <summary>Returns a successful response; a failed one is disposed and thrown as <see cref="Failure"/>.</summary>
    protected async Task<HttpResponseMessage> EnsureSuccessAsync(
        HttpResponseMessage response, CancellationToken ct, [CallerMemberName] string caller = "")
    {
        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw Failure(response, body, caller);
        }
    }

    /// <summary><see cref="SendRawAsync"/> then <see cref="EnsureSuccessAsync"/>.</summary>
    protected async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct, [CallerMemberName] string caller = "")
    {
        var response = await SendRawAsync(request, ct, caller);
        return await EnsureSuccessAsync(response, ct, caller);
    }

    /// <summary>
    /// Follows the cursor <paramref name="fetch"/> returns until it returns none. Throws at
    /// <paramref name="pageCap"/> or on a repeated cursor rather than ending early: these lists feed sweeps that
    /// delete whatever a fetch leaves out.
    /// </summary>
    protected async IAsyncEnumerable<TPage> GetPagesAsync<TPage>(
        string endpoint, Func<string?, Task<(TPage Page, string? Next)>> fetch, int pageCap,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 1; page <= pageCap; page++)
        {
            ct.ThrowIfCancellationRequested();
            var (items, next) = await fetch(cursor);
            yield return items;
            if (next is null)
                yield break;
            if (!seen.Add(next))
                throw Transient($"{ApiName} {endpoint}: a repeated cursor.");
            cursor = next;
        }
        throw Transient($"{ApiName} {endpoint}: hit the {pageCap.ToString(CultureInfo.InvariantCulture)}-page safety cap.");
    }
}

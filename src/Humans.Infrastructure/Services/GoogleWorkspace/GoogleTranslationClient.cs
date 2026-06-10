using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.Infrastructure.Services.GoogleWorkspace;

/// <summary>
/// Real <see cref="IGoogleTranslationClient"/> backed by the Cloud Translation v2 REST API,
/// authenticated with the same service account as the Workspace connectors (one more enabled API
/// on the existing GCP project — no new integration). Plain-text NMT; batched per request under
/// the API's 128-segment limit.
/// </summary>
public sealed class GoogleTranslationClient(
    HttpClient httpClient,
    IOptions<GoogleWorkspaceSettings> settings,
    ILogger<GoogleTranslationClient> logger) : IGoogleTranslationClient
{
    private const string Scope = "https://www.googleapis.com/auth/cloud-translation";
    private const string Endpoint = "https://translation.googleapis.com/language/translate/v2";
    private const int BatchSize = 100;

    public async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken ct = default)
    {
        if (texts.Count == 0) return [];

        var credential = await GoogleCredentialLoader.LoadScopedAsync(settings.Value, ct, Scope);
        var token = await credential.UnderlyingCredential.GetAccessTokenForRequestAsync(cancellationToken: ct);

        var results = new List<string>(texts.Count);
        foreach (var chunk in texts.Chunk(BatchSize))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(new TranslateRequest(chunk, sourceLanguage, targetLanguage, "text"));

            using var response = await httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning(
                    "Cloud Translation request failed ({StatusCode}) for {Source}→{Target}: {Body}",
                    (int)response.StatusCode, sourceLanguage, targetLanguage, body);
                throw new InvalidOperationException(
                    $"Translation to '{targetLanguage}' failed (HTTP {(int)response.StatusCode}). Is the Cloud Translation API enabled on the Google project?");
            }

            var payload = await response.Content.ReadFromJsonAsync<TranslateResponse>(ct)
                ?? throw new InvalidOperationException("Translation returned an empty response.");
            if (payload.Data.Translations.Count != chunk.Length)
            {
                throw new InvalidOperationException(
                    $"Translation returned {payload.Data.Translations.Count} segments for {chunk.Length} inputs.");
            }

            results.AddRange(payload.Data.Translations.Select(t => t.TranslatedText));
        }

        logger.LogInformation(
            "Translated {Count} segments {Source}→{Target}", texts.Count, sourceLanguage, targetLanguage);
        return results;
    }

    private sealed record TranslateRequest(IReadOnlyList<string> Q, string Source, string Target, string Format);

    private sealed record TranslateResponse(TranslateData Data);

    private sealed record TranslateData(IReadOnlyList<Translation> Translations);

    private sealed record Translation(string TranslatedText);
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using Humans.Base.Extensions;
using Humans.Base.Configuration;
using Microsoft.Extensions.Options;

namespace Humans.GoogleIntegration.Services.Workspace;

/// <summary>Batched under the API's 128-segment limit per request.</summary>
internal sealed class GoogleTranslationClient(
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
        using var _ = logger.TimeOperation();
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

            results.AddRange(await ReadTranslationsAsync(response.Content, chunk.Length, ct));
        }

        logger.LogInformation(
            "Translated {Count} segments {Source}→{Target}", texts.Count, sourceLanguage, targetLanguage);
        return results;
    }

    internal static async Task<IReadOnlyList<string>> ReadTranslationsAsync(
        HttpContent content, int expectedCount, CancellationToken ct)
    {
        var payload = await content.ReadFromJsonAsync<TranslateResponse>(ct)
            ?? throw new InvalidOperationException("Translation returned an empty response.");
        var translations = payload.Data?.Translations
            ?? throw new InvalidOperationException("Translation returned no segments.");
        if (translations.Count != expectedCount)
        {
            throw new InvalidOperationException(
                $"Translation returned {translations.Count} segments for {expectedCount} inputs.");
        }
        return translations.Select(t => t?.TranslatedText
            ?? throw new InvalidOperationException("Translation returned a segment without translated text.")).ToList();
    }

    private sealed record TranslateRequest(IReadOnlyList<string> Q, string Source, string Target, string Format);

    private sealed record TranslateResponse(TranslateData? Data);

    private sealed record TranslateData(IReadOnlyList<Translation?>? Translations);

    private sealed record Translation(string? TranslatedText);
}

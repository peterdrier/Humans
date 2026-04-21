using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Anthropic Claude Haiku client for the auto Consent Check job. Speaks to the
/// Messages API directly over HTTP — no SDK dependency. Configured via
/// <c>Anthropic:ApiKey</c> and (optionally) <c>Anthropic:Model</c>. Hard 10s
/// timeout, one retry on transient failure, structured JSON response.
/// </summary>
public sealed class ConsentCheckAssistant : IConsentCheckAssistant
{
    private const string DefaultModel = "claude-haiku-4-5";
    private const string AnthropicVersion = "2023-06-01";
    private const string MessagesEndpoint = "https://api.anthropic.com/v1/messages";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConsentCheckAssistant> _logger;

    public ConsentCheckAssistant(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<ConsentCheckAssistant> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ConsentCheckVerdict> EvaluateAsync(
        string legalName,
        IReadOnlyList<string> holdList,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(legalName))
        {
            throw new ArgumentException("Legal name is required.", nameof(legalName));
        }

        var apiKey = _configuration["Anthropic:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "Anthropic:ApiKey is not configured. Set Anthropic__ApiKey env var or user-secret.");
        }

        var model = _configuration["Anthropic:Model"];
        if (string.IsNullOrWhiteSpace(model))
        {
            model = DefaultModel;
        }

        var requestPayload = BuildRequest(model, legalName, holdList);
        var requestJson = JsonSerializer.Serialize(requestPayload);

        // One retry on transient/network failure.
        Exception? lastException = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(RequestTimeout);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, MessagesEndpoint);
                request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var response = await _httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);

                var body = await response.Content.ReadAsStringAsync(linkedCts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    // 5xx and 429 get one retry; 4xx is terminal.
                    var shouldRetry = attempt == 1 &&
                        ((int)response.StatusCode >= 500 || (int)response.StatusCode == 429);
                    _logger.LogWarning(
                        "Anthropic API returned {Status} on attempt {Attempt}: {Body}",
                        (int)response.StatusCode, attempt, Truncate(body, 500));
                    if (!shouldRetry)
                    {
                        throw new InvalidOperationException(
                            $"Anthropic API returned HTTP {(int)response.StatusCode}.");
                    }
                    continue;
                }

                return ParseResponse(body, model);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout (our linked token fired, not the caller's). Retry once.
                lastException = new TimeoutException(
                    $"Anthropic API call timed out after {RequestTimeout.TotalSeconds:F0}s.");
                _logger.LogWarning(
                    "Anthropic API call timed out on attempt {Attempt} after {Timeout}s",
                    attempt, RequestTimeout.TotalSeconds);
                if (attempt == 2) throw lastException;
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                _logger.LogWarning(
                    ex, "Anthropic API transport error on attempt {Attempt}", attempt);
                if (attempt == 2) throw;
            }
        }

        // Unreachable, but keeps the compiler happy.
        throw lastException ?? new InvalidOperationException("Anthropic API call failed.");
    }

    private static AnthropicRequest BuildRequest(
        string model, string legalName, IReadOnlyList<string> holdList)
    {
        var systemPrompt =
            "You are a safety classifier for a small volunteer-organisation signup flow. " +
            "Given the 'legal_name' field a new signup has put on their profile, answer " +
            "TWO yes/no questions as strict JSON:\n" +
            "1. plausible_real_name: does this look like a plausible real human name? " +
            "Reject obvious placeholders (asdf, test test, Mickey Mouse), profanity, " +
            "empty strings, single characters, ALL-CAPS gibberish. Accept real-sounding " +
            "names from any culture, including short ones, including ones with unusual " +
            "capitalisation or accents. When uncertain, lean towards ACCEPT — a human " +
            "coordinator will see anyone you reject.\n" +
            "2. hold_list_match: does this name match ANY entry in the hold_list I give " +
            "you? Match fuzzily — case-insensitive, tolerant of accent differences, " +
            "tolerant of first/last ordering, tolerant of diminutives that clearly refer " +
            "to the same person. If the hold list is empty, hold_list_match is always " +
            "false.\n\n" +
            "Respond with ONLY a single JSON object, no prose, no markdown fences:\n" +
            "{\"plausible_real_name\": bool, \"hold_list_match\": bool, \"reason\": \"short explanation\"}";

        var holdListText = holdList.Count == 0
            ? "(empty)"
            : string.Join("\n", holdList.Select(h => $"- {h}"));

        var userPrompt =
            $"legal_name: {legalName}\n\n" +
            $"hold_list:\n{holdListText}";

        return new AnthropicRequest(
            Model: model,
            MaxTokens: 256,
            System: systemPrompt,
            Messages: [new AnthropicMessage("user", userPrompt)]);
    }

    private ConsentCheckVerdict ParseResponse(string responseBody, string requestedModel)
    {
        AnthropicResponse? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<AnthropicResponse>(responseBody);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex, "Failed to parse Anthropic response envelope: {Body}", Truncate(responseBody, 500));
            throw new InvalidOperationException("Anthropic response was not valid JSON.", ex);
        }

        if (envelope is null || envelope.Content is null || envelope.Content.Count == 0)
        {
            throw new InvalidOperationException("Anthropic response had no content blocks.");
        }

        var textBlock = envelope.Content
            .FirstOrDefault(c => string.Equals(c.Type, "text", StringComparison.Ordinal));
        if (textBlock is null || string.IsNullOrWhiteSpace(textBlock.Text))
        {
            throw new InvalidOperationException("Anthropic response had no text block.");
        }

        var payloadText = ExtractJson(textBlock.Text);
        VerdictPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<VerdictPayload>(payloadText);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex, "Anthropic text block was not valid verdict JSON: {Text}", Truncate(textBlock.Text, 500));
            throw new InvalidOperationException(
                "Anthropic response body was not valid verdict JSON.", ex);
        }

        if (payload is null)
        {
            throw new InvalidOperationException("Anthropic response deserialised to null verdict.");
        }

        var reportedModel = string.IsNullOrWhiteSpace(envelope.Model) ? requestedModel : envelope.Model;

        return new ConsentCheckVerdict(
            PlausibleRealName: payload.PlausibleRealName,
            HoldListMatch: payload.HoldListMatch,
            Reason: string.IsNullOrWhiteSpace(payload.Reason) ? "(no reason)" : payload.Reason.Trim(),
            ModelId: reportedModel);
    }

    /// <summary>
    /// Strips optional ``` fences that small models sometimes add even when asked not to.
    /// </summary>
    private static string ExtractJson(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            // ```json\n...\n``` or ```\n...\n```
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0)
            {
                trimmed = trimmed[(firstNewline + 1)..];
            }
            var fenceEnd = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0)
            {
                trimmed = trimmed[..fenceEnd];
            }
            trimmed = trimmed.Trim();
        }
        return trimmed;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";

    // --- Wire types ---

    private sealed record AnthropicRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("system")] string System,
        [property: JsonPropertyName("messages")] IReadOnlyList<AnthropicMessage> Messages);

    private sealed record AnthropicMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed class AnthropicResponse
    {
        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("content")]
        public List<AnthropicContentBlock>? Content { get; set; }
    }

    private sealed class AnthropicContentBlock
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    private sealed class VerdictPayload
    {
        [JsonPropertyName("plausible_real_name")]
        public bool PlausibleRealName { get; set; }

        [JsonPropertyName("hold_list_match")]
        public bool HoldListMatch { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }
}

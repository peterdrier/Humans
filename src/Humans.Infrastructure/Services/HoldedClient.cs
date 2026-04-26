using System.Net.Http.Json;
using System.Text.Json;
using Humans.Application.Configuration;
using Humans.Application.DTOs.Finance;
using Humans.Application.Interfaces.Finance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Typed HttpClient wrapper for Holded's purchase-document endpoints.
/// API key from env var HOLDED_API_KEY (bound into HoldedSettings at startup);
/// never logged.
/// </summary>
public sealed class HoldedClient : IHoldedClient
{
    private const int PageSize = 100;
    private readonly HttpClient _http;
    private readonly ILogger<HoldedClient> _logger;
    private readonly HoldedSettings _settings;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public HoldedClient(HttpClient http, IOptions<HoldedSettings> settings, ILogger<HoldedClient> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;

        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri("https://api.holded.com/");

        if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            // Holded auth: 'key' header.
            _http.DefaultRequestHeaders.Remove("key");
            _http.DefaultRequestHeaders.Add("key", _settings.ApiKey);
        }
    }

    public async Task<IReadOnlyList<(HoldedDocDto Dto, string RawJson)>> GetAllPurchaseDocsAsync(CancellationToken ct = default)
    {
        var results = new List<(HoldedDocDto, string)>();
        var page = 1;

        while (true)
        {
            var url = $"api/invoicing/v1/documents/purchase?page={page}&limit={PageSize}";
            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            var raw = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                break;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var dto = element.Deserialize<HoldedDocDto>(JsonOptions)
                    ?? throw new InvalidOperationException("Failed to deserialize Holded purchase doc");
                var rawElementJson = element.GetRawText();
                results.Add((dto, rawElementJson));
            }

            page++;
        }

        _logger.LogInformation("Fetched {Count} Holded purchase docs across {Pages} page(s)", results.Count, page);
        return results;
    }

    public async Task<bool> TryAddTagAsync(string holdedDocId, string tag, CancellationToken ct = default)
    {
        try
        {
            // GET current doc to read existing tags
            using var getResp = await _http.GetAsync($"api/invoicing/v1/documents/purchase/{holdedDocId}", ct);
            if (!getResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Holded GET {DocId} returned {Status}; cannot add tag", holdedDocId, getResp.StatusCode);
                return false;
            }
            var current = await getResp.Content.ReadFromJsonAsync<HoldedDocDto>(JsonOptions, ct);
            if (current is null) return false;

            var newTags = current.Tags.Append(tag).Distinct(StringComparer.Ordinal).ToList();
            var payload = new { tags = newTags };

            using var putResp = await _http.PutAsJsonAsync($"api/invoicing/v1/documents/purchase/{holdedDocId}", payload, ct);
            if (!putResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Holded PUT {DocId} tag update returned {Status}", holdedDocId, putResp.StatusCode);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            // Always log problems even when expected (per project rule).
            _logger.LogWarning(ex, "Holded tag push for {DocId} failed", holdedDocId);
            return false;
        }
    }
}

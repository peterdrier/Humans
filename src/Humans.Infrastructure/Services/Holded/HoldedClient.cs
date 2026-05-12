using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Humans.Application.Interfaces.Holded;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Infrastructure.Services.Holded;

public sealed class HoldedClient : IHoldedClient
{
    private readonly HttpClient _http;
    private readonly HoldedClientOptions _options;
    private readonly ILogger<HoldedClient> _logger;

    public HoldedClient(
        HttpClient http,
        IOptions<HoldedClientOptions> options,
        ILogger<HoldedClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;

        if (_http.BaseAddress is null && !string.IsNullOrEmpty(_options.BaseUrl))
            _http.BaseAddress = new Uri(_options.BaseUrl);
    }

    public async Task<string> CreatePurchaseDocumentAsync(
        HoldedPurchaseDocumentInput input, CancellationToken ct = default)
    {
        var payload = new
        {
            contactName = input.ContactName,
            date = input.Date.ToUnixTimeSeconds(),
            desc = input.Description,
            tags = input.Tags,
            items = input.Lines.Select(l => new
            {
                name = l.Description,
                units = 1,
                subtotal = l.Amount,
                tags = l.Tags
            })
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            "/api/invoicing/v1/documents/purchase")
        { Content = JsonContent.Create(payload) };
        AttachAuth(req);

        using var resp = await SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var node = JsonNode.Parse(body)
            ?? throw new HoldedTransientException("Holded returned empty body");
        var id = node["id"]?.GetValue<string>()
            ?? throw new HoldedTransientException("Holded response missing id");
        return id;
    }

    public async Task UpdatePurchaseDocumentTagsAsync(
        string documentId, IReadOnlyList<string> tags, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put,
            $"/api/invoicing/v1/documents/purchase/{documentId}")
        { Content = JsonContent.Create(new { tags }) };
        AttachAuth(req);

        using var resp = await SendAsync(req, ct);
    }

    public async Task UploadAttachmentAsync(
        string documentId, HoldedAttachmentInput attachment, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(attachment.Content);
        streamContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(attachment.ContentType);
        content.Add(streamContent, "file", attachment.FileName);

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/invoicing/v1/documents/purchase/{documentId}/attach")
        { Content = content };
        AttachAuth(req);

        using var resp = await SendAsync(req, ct);
    }

    public async Task<HoldedPurchaseDocumentDto> GetPurchaseDocumentAsync(
        string documentId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"/api/invoicing/v1/documents/purchase/{documentId}");
        AttachAuth(req);

        using var resp = await SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var node = await JsonNode.ParseAsync(stream, cancellationToken: ct)
            ?? throw new HoldedTransientException("Holded returned empty body");

        return new HoldedPurchaseDocumentDto
        {
            Id = node["id"]?.GetValue<string>() ?? "",
            DocNumber = node["docNumber"]?.GetValue<string>() ?? "",
            Subtotal = ReadDecimal(node["subtotal"]),
            Tax = ReadDecimal(node["tax"]),
            Total = ReadDecimal(node["total"]),
            PaymentsTotal = ReadDecimal(node["paymentsTotal"]),
            PaymentsPending = ReadDecimal(node["paymentsPending"]),
            ApprovedAt = ReadInstant(node["approvedAt"]),
            Tags = node["tags"]?.AsArray()
                .Select(n => n!.GetValue<string>())
                .ToList() ?? []
        };
    }

    private void AttachAuth(HttpRequestMessage req) =>
        req.Headers.Add("key", _options.ApiKey);

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new HoldedTransientException("Holded HTTP send failed", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new HoldedTransientException("Holded HTTP send timed out", ex);
        }

        if (resp.IsSuccessStatusCode) return resp;

        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if ((int)resp.StatusCode >= 500)
                throw new HoldedTransientException(
                    $"Holded {(int)resp.StatusCode} {resp.ReasonPhrase}");
            throw new HoldedPermanentException((int)resp.StatusCode, body,
                $"Holded {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
        }
    }

    private static decimal ReadDecimal(JsonNode? node) =>
        node?.GetValue<decimal>() ?? 0m;

    private static Instant? ReadInstant(JsonNode? node)
    {
        if (node is null) return null;
        var seconds = node.GetValue<long>();
        return seconds == 0 ? null : Instant.FromUnixTimeSeconds(seconds);
    }
}

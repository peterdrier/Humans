using System.Net.Http.Json;
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
            contactId = input.ContactId, // TODO(probe): confirm field name (contactId vs contact)
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

    public async Task<IReadOnlyList<HoldedExpenseAccountDto>> ListExpenseAccountsAsync(
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/invoicing/v1/expensesaccounts");
        AttachAuth(req);
        using var resp = await SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var arr = (await JsonNode.ParseAsync(stream, cancellationToken: ct))?.AsArray() ?? [];
        return arr.Select(n => new HoldedExpenseAccountDto
        {
            Id = n!["id"]?.GetValue<string>() ?? "",
            AccountNum = (int)(n["accountNum"]?.GetValue<long>() ?? 0),
            Name = n["name"]?.GetValue<string>() ?? "",
        }).ToList();
    }

    public async Task<string> CreateExpenseAccountAsync(
        int accountNum, string name, CancellationToken ct = default)
    {
        // TODO(probe): confirm create-expenses-account payload field names against live API
        var payload = new { name, accountNum };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/invoicing/v1/expensesaccounts")
        { Content = JsonContent.Create(payload) };
        AttachAuth(req);
        using var resp = await SendAsync(req, ct);
        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))
            ?? throw new HoldedTransientException("Holded returned empty body");
        return node["id"]?.GetValue<string>()
            ?? throw new HoldedTransientException("Holded create-account response missing id");
    }

    public async Task<IReadOnlyList<HoldedPurchaseDocListItemDto>> ListPurchaseDocumentsPageAsync(
        int page, int limit, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"/api/invoicing/v1/documents/purchase?page={page}&limit={limit}");
        AttachAuth(req);
        using var resp = await SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var arr = (await JsonNode.ParseAsync(stream, cancellationToken: ct))?.AsArray() ?? [];
        return arr.Select(ParsePurchaseDoc).ToList();
    }

    public async Task<string> UpsertContactAsync(HoldedContactInput input, CancellationToken ct = default)
    {
        // TODO(probe): confirm contact payload field names (name/tradeName/customId/type/iban) against live API.
        var payload = new
        {
            name = input.Name,
            tradeName = input.TradeName,
            customId = input.CustomId,
            type = input.Type,
            iban = input.Iban,
        };

        var isUpdate = !string.IsNullOrEmpty(input.ExistingContactId);
        using var req = new HttpRequestMessage(
            isUpdate ? HttpMethod.Put : HttpMethod.Post,
            isUpdate
                ? $"/api/invoicing/v1/contacts/{input.ExistingContactId}"
                : "/api/invoicing/v1/contacts")
        { Content = JsonContent.Create(payload) };
        AttachAuth(req);

        using var resp = await SendAsync(req, ct);
        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))
            ?? throw new HoldedTransientException("Holded returned empty body");
        return node["id"]?.GetValue<string>()
            ?? input.ExistingContactId
            ?? throw new HoldedTransientException("Holded contact upsert response missing id");
    }

    public async Task<HoldedContactDto> GetContactAsync(string contactId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/invoicing/v1/contacts/{contactId}");
        AttachAuth(req);
        using var resp = await SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var node = await JsonNode.ParseAsync(stream, cancellationToken: ct)
            ?? throw new HoldedTransientException("Holded returned empty body");

        return new HoldedContactDto
        {
            Id = node["id"]?.GetValue<string>() ?? contactId,
            Name = node["name"]?.GetValue<string>(),
            SupplierAccountNum = ReadInt(node["supplierRecord"]?["num"]),
        };
    }

    public async Task<IReadOnlyList<HoldedChartAccountDto>> ListChartOfAccountsAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/accounting/v1/chartofaccounts");
        AttachAuth(req);
        using var resp = await SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var arr = (await JsonNode.ParseAsync(stream, cancellationToken: ct))?.AsArray() ?? [];
        return arr.Where(n => n is not null).Select(n => new HoldedChartAccountDto
        {
            Num = ReadInt(n!["num"]) ?? 0,
            Name = n["name"]?.GetValue<string>() ?? "",
            Balance = ReadDecimal(n["balance"]),
        }).ToList();
    }

    public async Task<IReadOnlyList<HoldedPaymentDto>> ListPaymentsAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/invoicing/v1/payments");
        AttachAuth(req);
        using var resp = await SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var arr = (await JsonNode.ParseAsync(stream, cancellationToken: ct))?.AsArray() ?? [];
        return arr.Where(n => n is not null).Select(n => new HoldedPaymentDto
        {
            Id = n!["id"]?.GetValue<string>() ?? "",
            ContactId = n["contactId"]?.GetValue<string>() ?? "",
            Amount = ReadDecimal(n["amount"]),
            Date = ReadInstant(n["date"]) ?? Instant.FromUnixTimeSeconds(0),
            DocumentType = n["documentType"]?.GetValue<string>(),
        }).ToList();
    }

    private static HoldedPurchaseDocListItemDto ParsePurchaseDoc(JsonNode? n) => new()
    {
        Id = n!["id"]?.GetValue<string>() ?? "",
        DocNumber = n["docNumber"]?.GetValue<string>() ?? "",
        ContactName = n["contactName"]?.GetValue<string>() ?? "",
        Date = ReadInstant(n["date"]) ?? Instant.FromUnixTimeSeconds(0),
        Subtotal = ReadDecimal(n["subtotal"]),
        Tax = ReadDecimal(n["tax"]),
        Total = ReadDecimal(n["total"]),
        ApprovedAt = ReadInstant(n["approvedAt"]),
        Currency = n["currency"]?.GetValue<string>() ?? "eur",
        Tags = ReadTags(n["tags"]),
        Lines = (n["products"]?.AsArray() ?? []).Select(p => new HoldedPurchaseLineDto
        {
            Amount = ReadDecimal(p!["price"]),
            AccountId = p["account"]?.GetValue<string>(),
            Tags = ReadTags(p["tags"]),
        }).ToList(),
    };

    private static IReadOnlyList<string> ReadTags(JsonNode? node) =>
        node?.AsArray().Where(t => t is not null).Select(t => t!.GetValue<string>()).ToList() ?? [];

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

    // GetValue<decimal> (not <long>) so a JSON float token like 40000001.0 parses; cast truncates.
    private static int? ReadInt(JsonNode? node) =>
        node is null ? null : (int?)node.GetValue<decimal>();

    private static Instant? ReadInstant(JsonNode? node)
    {
        if (node is null) return null;
        var seconds = node.GetValue<long>();
        return seconds == 0 ? null : Instant.FromUnixTimeSeconds(seconds);
    }
}

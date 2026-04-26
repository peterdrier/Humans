using System.Text.Json.Serialization;

namespace Humans.Application.DTOs.Finance;

/// <summary>
/// Wire DTO mirroring the Holded purchase-doc JSON. See spec section
/// "Holded API findings" for field semantics.
/// </summary>
public sealed class HoldedDocDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("docNumber")] public string DocNumber { get; set; } = string.Empty;
    [JsonPropertyName("contact")] public string? Contact { get; set; }
    [JsonPropertyName("contactName")] public string? ContactName { get; set; }
    [JsonPropertyName("date")] public long Date { get; set; }
    [JsonPropertyName("dueDate")] public long? DueDate { get; set; }
    [JsonPropertyName("accountingDate")] public long? AccountingDate { get; set; }
    [JsonPropertyName("approvedAt")] public long? ApprovedAt { get; set; }
    [JsonPropertyName("currency")] public string? Currency { get; set; }
    [JsonPropertyName("subtotal")] public decimal Subtotal { get; set; }
    [JsonPropertyName("tax")] public decimal Tax { get; set; }
    [JsonPropertyName("total")] public decimal Total { get; set; }
    [JsonPropertyName("paymentsTotal")] public decimal PaymentsTotal { get; set; }
    [JsonPropertyName("paymentsPending")] public decimal PaymentsPending { get; set; }
    [JsonPropertyName("paymentsRefunds")] public decimal PaymentsRefunds { get; set; }
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("from")] public HoldedFromDto? From { get; set; }
}

public sealed class HoldedFromDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("docType")] public string? DocType { get; set; }
}

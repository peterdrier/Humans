using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Humans.Holded.Contracts;
using static Humans.Holded.Services.HoldedJson;

namespace Humans.Holded.Services;

internal sealed class HoldedContactsClient(HoldedTransport transport, ILogger<HoldedClient> logger)
{
    public async Task<string> CreateContactAsync(HoldedContactInput input, CancellationToken ct = default)
    {
        // Create only: v2's contact PUT is a full replacement that resets supplier_record, so a partial PUT mints a
        // second creditor account. custom_id isn't sent (v2 has none). With no address parts bill_address stays
        // null, and OmitNulls drops the key.
        object? billAddress =
            string.IsNullOrWhiteSpace(input.Address) && string.IsNullOrWhiteSpace(input.CountryCode)
                ? null
                : new { address = input.Address, country_code = input.CountryCode };

        var payload = new
        {
            name = input.Name,
            trade_name = input.TradeName,
            type = input.Type,
            iban = input.Iban,
            code = input.TaxCode,
            email = input.Email,
            bill_address = billAddress,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v2/contacts")
        { Content = JsonContent.Create(payload, options: OmitNulls) };

        using var resp = await transport.SendAsync(req, ct);
        try
        {
            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))
                ?? throw new HoldedPermanentException("Holded returned no contact identity after accepting creation.");
            var id = node["id"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id)) return id;
            throw new HoldedPermanentException("Holded returned no contact identity after accepting creation.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            throw new HoldedPermanentException(
                "Holded create-contact response could not be read.", ex);
        }
    }

    public async Task<HoldedContactDto> GetContactAsync(string contactId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v2/contacts/{contactId}");
        using var resp = await transport.SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        try
        {
            var node = await JsonNode.ParseAsync(stream, cancellationToken: ct)
                ?? throw new HoldedTransientException("Holded returned empty body");

            return ParseContact(node, contactId);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            // The bad value belongs to the stored contact, so retrying can't help. Permanent, so callers that handle
            // the typed exceptions degrade; a raw parse failure would abort ExpenseHoldedPublisher's whole outbox batch.
            throw new HoldedPermanentException(
                $"Holded contact {contactId} could not be read.", ex);
        }
    }

    public async Task DeleteContactAsync(string contactId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/v2/contacts/{contactId}");
        try
        {
            using var resp = await transport.SendAsync(req, ct);
        }
        // The goal state already holds: a retried cleanup finds the contact gone.
        catch (HoldedPermanentException ex) when (ex.StatusCode == 404)
        {
            // The id was created seconds ago by this process, so a 404 most likely means the DELETE
            // route is wrong and the contact still exists in Holded.
            logger.LogError(
                "Holded contact {ContactId} answered 404 to DELETE — it was just created by this process, " +
                "so the route is most likely wrong and the contact still exists in Holded; treated as deleted",
                contactId);
        }
    }

    public async Task<IReadOnlyList<HoldedContactDto>> ListContactsAsync(CancellationToken ct = default)
    {
        const int pageSafetyCap = 50; // 10 000+ contacts (limit=200/page) — far above a small nonprofit's vendor/member list
        List<JsonNode> items;
        try
        {
            items = await transport.GetPagedAsync("/api/v2/contacts?limit=200", pageSafetyCap, ct);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            // The page envelope was unreadable; the skip-and-log below covers only one bad item in a good page.
            throw new HoldedPermanentException("Holded contacts could not be read.", ex);
        }

        var contacts = new List<HoldedContactDto>();
        var skipped = 0;
        foreach (var n in items)
        {
            try
            {
                contacts.Add(ParseContact(n));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException
                or FormatException or OverflowException)
            {
                // One bad contact must not cost the others their names: this list is the only source of creditor
                // names on /Finance/Creditors, and its caller turns a throw into all names blank. Detail is logged
                // for the first only, so a bad page can't flood the log.
                if (skipped == 0)
                    logger.LogWarning(ex, "Unreadable Holded contact; skipping it.");
                skipped++;
            }
        }

        if (skipped > 0)
            logger.LogWarning(
                "Skipped {Skipped} unreadable Holded contact(s); those accounts will show without a name.",
                skipped);
        return contacts;
    }

    /// <summary>Holded sends an absent sub-record as an empty array, and the string indexer throws on anything but an
    /// object, so every nested read goes through <see cref="Prop"/>.</summary>
    private static HoldedContactDto ParseContact(JsonNode node, string? fallbackId = null) => new()
    {
        Id = Prop(node, "id")?.GetValue<string>() ?? fallbackId ?? "",
        Name = Prop(node, "name")?.GetValue<string>(),
        SupplierAccountNum = ReadInt(Prop(Prop(node, "supplier_record"), "num")),
        TradeName = Prop(node, "trade_name")?.GetValue<string>(),
        Email = Prop(node, "email")?.GetValue<string>(),
        Phone = Prop(node, "phone")?.GetValue<string>(),
        Mobile = Prop(node, "mobile")?.GetValue<string>(),
        Iban = Prop(node, "iban")?.GetValue<string>(),
        TaxCode = Prop(node, "code")?.GetValue<string>(),
        Address = FormatAddress(Prop(node, "bill_address")),
    };

    /// <summary>Null when the address is absent or empty.</summary>
    private static string? FormatAddress(JsonNode? billAddress)
    {
        var parts = new[] { "address", "postal_code", "city", "province", "country" }
            .Select(k => Prop(billAddress, k)?.GetValue<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v));
        var joined = string.Join(", ", parts);
        return joined.Length == 0 ? null : joined;
    }
}

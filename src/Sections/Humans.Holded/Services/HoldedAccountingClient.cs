using Humans.Base.Extensions;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Humans.Holded.Contracts;
using NodaTime;
using NodaTime.Text;
using static Humans.Holded.Services.HoldedJson;

namespace Humans.Holded.Services;

internal sealed class HoldedAccountingClient(HoldedTransport transport, ILogger<HoldedClient> logger)
{
    public async Task<string> PostLedgerEntryAsync(
        LocalDate date, int debitAccount, int creditAccount, decimal amount, string description,
        CancellationToken ct = default)
    {
        // Amounts are decimal strings and accounts are ledger numbers, as for payments. The date goes out ISO;
        // DD/MM/YYYY is only how ledger-entries reads come back.
        var money = amount.ToString("F2", CultureInfo.InvariantCulture);
        var payload = new
        {
            date = LocalDatePattern.Iso.Format(date),
            notes = description,
            lines = new[]
            {
                new { account = debitAccount, description, debit = money, credit = "0.00" },
                new { account = creditAccount, description, debit = "0.00", credit = money },
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v2/ledger-entries")
        { Content = JsonContent.Create(payload, options: OmitNulls) };

        using var resp = await transport.SendAsync(req, ct);
        string? id = null;
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            id = JsonNode.Parse(body)?["id"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            logger.LogWarning(ex,
                "Holded accepted a ledger entry (debit {Debit}, credit {Credit}, {Amount}) but its response could not be parsed.",
                debitAccount, creditAccount, money);
        }

        if (!string.IsNullOrWhiteSpace(id)) return id;

        // Posted, like the payment case: a throw here would let the caller post it again.
        logger.LogWarning(
            "Holded accepted a ledger entry (debit {Debit}, credit {Credit}, {Amount}) without a readable id; recording it as unconfirmed.",
            debitAccount, creditAccount, money);
        return UnconfirmedPaymentRefPrefix + "entry";
    }

    public async Task<IReadOnlyList<HoldedExpenseAccountDto>> ListExpenseAccountsAsync(
        CancellationToken ct = default)
    {
        const int pageSafetyCap = 5; // a handful of expense accounts today, unpaginated in practice
        try
        {
            var items = await transport.GetPagedAsync("/api/v2/expenses-accounts?limit=200", pageSafetyCap, ct);
            return items.Select(n => new HoldedExpenseAccountDto
            {
                Id = Prop(n, "id")?.GetValue<string>() ?? "",
                AccountNum = ReadRequiredInt(Prop(n, "account_num"), "account_num"),
                Name = Prop(n, "name")?.GetValue<string>() ?? "",
            }).ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            throw new HoldedPermanentException("Holded expense accounts could not be read.", ex);
        }
    }

    public async Task<string> CreateExpenseAccountAsync(
        int accountNum, string name, CancellationToken ct = default)
    {
        var payload = new { name, account_num = accountNum };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v2/expenses-accounts")
        { Content = JsonContent.Create(payload, options: OmitNulls) };
        using var resp = await transport.SendAsync(req, ct);
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            var node = JsonNode.Parse(body)
                ?? throw new HoldedPermanentException("Holded returned no expense-account identity after accepting creation.");
            var id = node["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id))
                throw new HoldedPermanentException("Holded returned no expense-account identity after accepting creation.");
            return id;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            throw new HoldedPermanentException(
                "Holded create-account response could not be read.", ex);
        }
    }

    public async Task<IReadOnlyList<HoldedLedgerLineDto>> ListLedgerEntriesAsync(
        LocalDate from, LocalDate to, int? accountNum = null, CancellationToken ct = default)
    {
        const int pageSafetyCap = 100; // 20 000 lines/window — far above a small nonprofit's volume
        // end_date is exclusive on the live API — send to+1 so the inclusive-`to` contract holds.
        var query =
            $"/api/v2/ledger-entries?start_date={LocalDatePattern.Iso.Format(from)}" +
            $"&end_date={LocalDatePattern.Iso.Format(to.PlusDays(1))}&limit=200";
        if (accountNum is { } num)
            query += $"&account={num}";

        var items = await transport.GetPagedAsync(query, pageSafetyCap, ct);
        try
        {
            return items.Select(n => new HoldedLedgerLineDto
            {
                // A manufactured 0 key would survive into ReplaceLedgerWindowAsync and evict the real row.
                EntryNumber = ReadRequiredInt(Prop(n, "entry_number"), "entry_number"),
                Line = ReadRequiredInt(Prop(n, "line"), "line"),
                Date = ParseLedgerDate(Prop(n, "date")?.GetValue<string>() ?? ""),
                AccountNum = ReadRequiredInt(Prop(n, "account"), "account"),
                Debit = ReadRequiredDecimalV2(Prop(n, "debit"), "debit"),
                Credit = ReadRequiredDecimalV2(Prop(n, "credit"), "credit"),
                Type = Prop(n, "type")?.GetValue<string>(),
                Description = Prop(n, "description")?.GetValue<string>(),
            }).ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException or UnparsableValueException)
        {
            // Fails the whole page rather than skipping the line: balances are summed from these, so a dropped
            // line reads as a settled entry that never happened.
            throw new HoldedPermanentException(
                $"Holded ledger-entries {from}..{to} could not be read.", ex);
        }
    }

    public async Task<IReadOnlyList<HoldedAccountDto>> ListAccountingAccountsAsync(
        CancellationToken ct = default)
    {
        const int pageSafetyCap = 5; // a few hundred accounts, unpaginated in practice — headroom
        var items = await transport.GetPagedAsync("/api/v2/accounting-accounts?limit=200", pageSafetyCap, ct);
        try
        {
            return items.Select(n => new HoldedAccountDto
            {
                Id = ReadRequiredString(Prop(n, "id"), "id"),
                // Required: the number keys the mirror, picks the PGC group and drives the POV flip. A manufactured 0
                // would become an "Unclassified" account with a sign-flipped balance, reconciled every night.
                Number = ReadRequiredInt(Prop(n, "number"), "number"),
                Name = Prop(n, "name")?.GetValue<string>() ?? "",
                Group = Prop(n, "group")?.GetValue<string>(),
                Debit = ReadRequiredDecimalV2(Prop(n, "debit"), "debit"),
                Credit = ReadRequiredDecimalV2(Prop(n, "credit"), "credit"),
                Balance = ReadRequiredDecimalV2(Prop(n, "balance"), "balance"),
                Archived = Prop(n, "archived")?.GetValue<bool>() ?? false,
            }).ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            throw new HoldedPermanentException("Holded accounting-accounts could not be read.", ex);
        }
    }

    public async Task<HoldedUsageDto> GetUsageAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v2/usage");
        using var resp = await transport.SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        try
        {
            // Parse inside the try: a truncated 200 body must surface as a typed Holded
            // exception so /Holded degrades to "unreachable" instead of a raw 500.
            var node = await JsonNode.ParseAsync(stream, cancellationToken: ct)
                ?? throw new HoldedTransientException("Holded returned empty body");
            var secondary = Prop(node, "secondary_usages") as JsonObject;
            return new HoldedUsageDto
            {
                Period = Prop(node, "period")?.GetValue<string>() ?? "",
                Usage = Prop(node, "usage")?.GetValue<long>() ?? 0,
                Limit = Prop(node, "limit")?.GetValue<long>() ?? 0,
                SecondaryUsages = secondary is null
                    ? new Dictionary<string, long>(StringComparer.Ordinal)
                    : secondary.ToDictionary(
                        kv => kv.Key, kv => kv.Value?.GetValue<long>() ?? 0, StringComparer.Ordinal),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            throw new HoldedPermanentException("Holded usage could not be read.", ex);
        }
    }

    private static Instant ParseLedgerDate(string s) =>
        DateFormattingExtensions.HoldedLedgerDatePattern.Parse(s).Value
            .AtStartOfDayInZone(MadridZone).ToInstant();
}

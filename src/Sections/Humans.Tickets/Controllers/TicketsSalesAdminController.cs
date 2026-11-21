using System.Globalization;
using Humans.Base.Controllers;
using Humans.Grants.Contracts;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Text;

namespace Humans.Tickets.Controllers;

/// <summary>Per-event sales setup (backend, mode, waves, price rules) and the orders it took. English only.</summary>
[Authorize(Policy = TicketsPolicies.Read)]
[Route("Tickets/Admin/Sales")]
internal sealed class TicketsSalesAdminController(
    ITicketCheckoutService checkout,
    ISettingsService settings,
    IGrantService grants,
    IUserService userService) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var events = await settings.GetAllEventSettingsAsync(ct);
        var salesEvents = await checkout.GetSalesEventsAsync(ct);
        return View("~/Views/Tickets/Admin/Sales/Index.cshtml",
            new SalesAdminIndexViewModel(salesEvents, events, checkout.Backends));
    }

    [HttpGet("Backends")]
    public async Task<IActionResult> Backends(CancellationToken ct) =>
        View("~/Views/Tickets/Admin/Sales/Backends.cshtml", await BackendsModelAsync(null, ct));

    /// <summary>Changes nothing; Manage only because it calls the vendor with Humans' credentials.</summary>
    [HttpPost("Backends/{key}/Check")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckBackend(string key, string? backendEventRef, CancellationToken ct)
    {
        var findings = await checkout.CheckBackendAsync(key, backendEventRef, ct);
        if (findings is null)
            return NotFound();
        return View("~/Views/Tickets/Admin/Sales/Backends.cshtml",
            await BackendsModelAsync(new BackendCheckResult(key, backendEventRef, findings), ct));
    }

    [HttpGet("Edit/{id:guid?}")]
    public async Task<IActionResult> Edit(Guid? id, CancellationToken ct)
    {
        var salesEvent = id is { } existing ? await checkout.GetSalesEventAsync(existing, ct) : null;
        if (id is not null && salesEvent is null)
            return NotFound();
        return View("~/Views/Tickets/Admin/Sales/Edit.cshtml", await EditModelAsync(SalesEditForm.From(salesEvent), null, ct));
    }

    [HttpPost("Edit/{id:guid?}")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid? id, SalesEditForm form, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var (save, parseError) = form.ToSave(id);
        var result = save is null ? new CheckoutMutationResult(parseError) : await checkout.SaveSalesEventAsync(save, user.Id, ct);
        if (!result.Succeeded)
            return View("~/Views/Tickets/Admin/Sales/Edit.cshtml", await EditModelAsync(form, result.Refusal, ct));

        SetSuccess("Sales setup saved.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("{id:guid}/Orders")]
    public async Task<IActionResult> Orders(Guid id, CancellationToken ct)
    {
        var salesEvent = await checkout.GetSalesEventAsync(id, ct);
        if (salesEvent is null)
            return NotFound();
        var orders = await checkout.GetOrdersForSalesEventAsync(id, ct);
        return View("~/Views/Tickets/Admin/Sales/Orders.cshtml", (salesEvent, orders));
    }

    [HttpGet("Order/{id:guid}")]
    public async Task<IActionResult> Order(Guid id, CancellationToken ct)
    {
        // Picks up refunds made in the backend's own dashboard.
        if (await checkout.RefreshOrderAsync(id, ct) is null)
            return NotFound();
        var detail = await checkout.GetOrderDetailAsync(id, ct);
        return View("~/Views/Tickets/Admin/Sales/Order.cshtml", detail!);
    }

    [HttpPost("Order/{id:guid}/Refund")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Refund(Guid id, decimal amount, string? reason, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var result = await checkout.RefundAsync(
            id, (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero), reason ?? string.Empty, user.Id, ct);
        if (result.Succeeded)
            SetSuccess("Refund sent.");
        else
            SetError(result.Refusal!);
        return RedirectToAction(nameof(Order), new { id });
    }

    private async Task<BackendsViewModel> BackendsModelAsync(BackendCheckResult? check, CancellationToken ct) =>
        new(checkout.Backends, await checkout.GetSalesEventsAsync(ct), await settings.GetAllEventSettingsAsync(ct), check);

    private async Task<SalesEditViewModel> EditModelAsync(SalesEditForm form, string? error, CancellationToken ct) =>
        new(form, error, await settings.GetAllEventSettingsAsync(ct), await grants.GetProgramSummariesAsync(ct), checkout.Backends);
}

internal sealed record SalesAdminIndexViewModel(
    IReadOnlyList<TicketSalesEvent> SalesEvents,
    IReadOnlyList<EventSettingsInfo> Events,
    IReadOnlyList<CheckoutBackendInfo> Backends)
{
    public string EventName(Guid id) => Events.FirstOrDefault(e => e.Id == id)?.EventName ?? id.ToString();
}

internal sealed record BackendsViewModel(
    IReadOnlyList<CheckoutBackendInfo> Backends,
    IReadOnlyList<TicketSalesEvent> SalesEvents,
    IReadOnlyList<EventSettingsInfo> Events,
    BackendCheckResult? Check)
{
    public string EventName(Guid id) => Events.FirstOrDefault(e => e.Id == id)?.EventName ?? id.ToString();
}

internal sealed record BackendCheckResult(string Key, string? BackendEventRef, IReadOnlyList<CheckoutBackendFinding> Findings);

internal sealed record SalesEditViewModel(
    SalesEditForm Form,
    string? Error,
    IReadOnlyList<EventSettingsInfo> Events,
    IReadOnlyList<GrantProgramSummary> Programs,
    IReadOnlyList<CheckoutBackendInfo> Backends);

/// <summary>
/// The lists are one entry per line, <c>;</c>-separated, amounts in euros: waves <c>name;end date;price</c>, rules
/// <c>grant program id;ClassPrice|Credit;amount;label</c>. Supporter totals are comma-separated euros.
/// </summary>
internal sealed class SalesEditForm
{
    public Guid EventSettingsId { get; set; }
    public string BackendKey { get; set; } = string.Empty;
    public string? BackendEventRef { get; set; }
    public TicketSalesMode Mode { get; set; }
    public string? SalesCloseAt { get; set; }
    public int Capacity { get; set; } = 3000;
    public decimal TicketVatRate { get; set; } = 0.10m;
    public string? Waves { get; set; }
    public string? SupporterTotals { get; set; }
    public string? PriceRules { get; set; }
    public string? ChildPrice { get; set; }
    public int? ChildMaxAge { get; set; }
    public int? ResaleOpensAtSold { get; set; }
    public int DirectedResaleDays { get; set; } = 3;
    public string? TermsUrl { get; set; }

    private static readonly LocalDatePattern DatePattern = LocalDatePattern.Iso;
    private static readonly InstantPattern InstantPattern = InstantPattern.General;

    public static SalesEditForm From(TicketSalesEvent? e) => e is null ? new() : new()
    {
        EventSettingsId = e.EventSettingsId,
        BackendKey = e.BackendKey,
        BackendEventRef = e.BackendEventRef,
        Mode = e.Mode,
        SalesCloseAt = e.SalesCloseAt is { } close ? InstantPattern.Format(close) : null,
        Capacity = e.Capacity,
        TicketVatRate = e.TicketVatRate,
        Waves = string.Join('\n', e.Waves.Select(w => $"{w.Name};{DatePattern.Format(w.EndsOn)};{Eur(w.PriceMinor)}")),
        SupporterTotals = string.Join(", ", e.SupporterTotalsMinor.Select(Eur)),
        PriceRules = string.Join('\n', e.PriceRules.Select(r => $"{r.GrantProgramId};{r.Kind};{Eur(r.AmountMinor)};{r.Label}")),
        ChildPrice = e.ChildPriceMinor is { } child ? Eur(child) : null,
        ChildMaxAge = e.ChildMaxAge,
        ResaleOpensAtSold = e.ResaleOpensAtSold,
        DirectedResaleDays = e.DirectedResaleDays,
        TermsUrl = e.TermsUrl,
    };

    public (SalesEventSave? Save, string? Error) ToSave(Guid? id)
    {
        Instant? close = null;
        if (!string.IsNullOrWhiteSpace(SalesCloseAt))
        {
            var parsed = InstantPattern.Parse(SalesCloseAt.Trim());
            if (!parsed.Success) return (null, "Sales close is an instant like 2027-05-25T22:00:00Z.");
            close = parsed.Value;
        }

        var waves = new List<TicketSalesWave>();
        foreach (var line in Lines(Waves))
        {
            var parts = line.Split(';', StringSplitOptions.TrimEntries);
            if (parts.Length != 3 || !DatePattern.Parse(parts[1]).TryGetValue(default, out var endsOn) || !TryMinor(parts[2], out var price))
                return (null, $"Wave line '{line}' is not name;yyyy-MM-dd;price.");
            waves.Add(new TicketSalesWave { Name = parts[0], EndsOn = endsOn, PriceMinor = price });
        }

        var totals = new List<long>();
        foreach (var part in (SupporterTotals ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryMinor(part, out var total)) return (null, $"Supporter total '{part}' is not an amount.");
            totals.Add(total);
        }

        var rules = new List<TicketPriceRule>();
        foreach (var line in Lines(PriceRules))
        {
            var parts = line.Split(';', 4, StringSplitOptions.TrimEntries);
            if (parts.Length != 4 || !Guid.TryParse(parts[0], out var programId)
                || !Enum.TryParse<TicketPriceRuleKind>(parts[1], ignoreCase: true, out var kind) || !TryMinor(parts[2], out var amount))
                return (null, $"Price rule line '{line}' is not programId;ClassPrice|Credit|WavePin;amount;label.");
            rules.Add(new TicketPriceRule { GrantProgramId = programId, Kind = kind, AmountMinor = amount, Label = parts[3] });
        }

        long? childPrice = null;
        if (!string.IsNullOrWhiteSpace(ChildPrice))
        {
            if (!TryMinor(ChildPrice.Trim(), out var child)) return (null, "Child ticket price is not an amount.");
            childPrice = child;
        }

        return (new SalesEventSave(id, EventSettingsId, BackendKey.Trim(), BackendEventRef, Mode, close, Capacity,
            TicketVatRate, waves, totals, rules)
        {
            ChildPriceMinor = childPrice,
            ChildMaxAge = ChildMaxAge,
            ResaleOpensAtSold = ResaleOpensAtSold,
            DirectedResaleDays = DirectedResaleDays,
            TermsUrl = string.IsNullOrWhiteSpace(TermsUrl) ? null : TermsUrl.Trim(),
        }, null);
    }

    private static IEnumerable<string> Lines(string? text) =>
        (text ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static bool TryMinor(string text, out long minor)
    {
        minor = 0;
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var eur)) return false;
        minor = (long)Math.Round(eur * 100m, MidpointRounding.AwayFromZero);
        return true;
    }

    private static string Eur(long minor) => (minor / 100m).ToString("0.00", CultureInfo.InvariantCulture);
}

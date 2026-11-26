using System.Globalization;
using Humans.Base.Controllers;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Grants.Contracts;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using Humans.Tickets.Services.Delivery;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Text;

namespace Humans.Tickets.Controllers;

/// <summary>Per-event ticketing settings (ticketing platform, sales mode, waves, price rules) and the orders they took. English only.</summary>
[Authorize(Policy = TicketsPolicies.Read)]
[Route("Tickets/Admin/Sales")]
internal sealed class TicketsSalesAdminController(
    ITicketCheckoutService checkout,
    TicketDeliveryService delivery,
    ISettingsService settings,
    IGrantService grants,
    IOrgTimeZone orgTimeZone,
    IClock clock,
    IUserService userService) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var events = await settings.GetAllEventSettingsAsync(ct);
        var allSettings = await checkout.ListTicketingSettingsAsync(ct);
        return View("~/Views/Tickets/Admin/Sales/Index.cshtml", new SalesAdminIndexViewModel(
            allSettings, events, PlatformsForDisplay, clock.GetCurrentInstant().InZone(orgTimeZone.Zone).Date));
    }

    [HttpGet("Platforms")]
    public async Task<IActionResult> Platforms(CancellationToken ct) =>
        View("~/Views/Tickets/Admin/Sales/Platforms.cshtml", await PlatformsModelAsync(ct));

    [HttpGet("Edit/{id:guid?}")]
    public async Task<IActionResult> Edit(Guid? id, CancellationToken ct)
    {
        var ticketing = id is { } existing ? await checkout.GetTicketingSettingsAsync(existing, ct) : null;
        if (id is not null && ticketing is null)
            return NotFound();
        return View("~/Views/Tickets/Admin/Sales/Edit.cshtml",
            await EditModelAsync(SalesEditForm.From(ticketing, orgTimeZone.Zone), null, ct));
    }

    [HttpPost("Edit/{id:guid?}")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid? id, SalesEditForm form, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var (save, parseError) = form.ToSave(id, orgTimeZone.Zone);
        var result = save is null ? new CheckoutMutationResult(parseError) : await checkout.SaveTicketingSettingsAsync(save, user.Id, ct);
        if (!result.Succeeded)
            return View("~/Views/Tickets/Admin/Sales/Edit.cshtml", await EditModelAsync(form, result.Refusal, ct));

        SetSuccess("Ticketing settings saved.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("{id:guid}/Orders")]
    public async Task<IActionResult> Orders(Guid id, CancellationToken ct)
    {
        var ticketing = await checkout.GetTicketingSettingsAsync(id, ct);
        if (ticketing is null)
            return NotFound();
        IReadOnlyList<TicketCheckoutOrder> orders = (await checkout.GetOrdersForSettingsAsync(id, ct))
            .OrderByDescending(order => order.CreatedAt).ToList();
        var eventName = (await settings.GetEventSettingsByIdAsync(ticketing.EventSettingsId, ct))?.EventName
            ?? ticketing.EventSettingsId.ToString();
        return View("~/Views/Tickets/Admin/Sales/Orders.cshtml", (ticketing, eventName, orders));
    }

    [HttpGet("Order/{id:guid}")]
    public async Task<IActionResult> Order(Guid id, CancellationToken ct)
    {
        // Picks up refunds made in the platform's own dashboard.
        if (await checkout.RefreshOrderAsync(id, ct) is null)
            return NotFound();
        var detail = await checkout.GetOrderDetailAsync(id, ct);
        if (detail is null)
            return NotFound();
        return View("~/Views/Tickets/Admin/Sales/Order.cshtml", detail with
        {
            Tickets = detail.Tickets.OrderBy(ticket => ticket.Kind).ThenBy(ticket => ticket.IssuedAt).ToList(),
            Refunds = detail.Refunds.OrderBy(refund => refund.CreatedAt).ToList(),
        });
    }

    [HttpPost("Order/{id:guid}/Refund")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Refund(Guid id, decimal amount, string? reason, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var result = await checkout.RefundAsync(
            id, (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero), reason ?? string.Empty, user.Id, CancellationToken.None); // a refund sent must be recorded even after a disconnect
        if (result.Succeeded)
            SetSuccess("Refund sent.");
        else
            SetError(result.Refusal!);
        return RedirectToAction(nameof(Order), new { id });
    }

    /// <summary>A holder says the ticket never arrived: every ticket email and the receipt go out again.</summary>
    [HttpPost("Order/{id:guid}/Resend")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resend(Guid id, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var result = await delivery.ResendAsync(id, user.Id, ct);
        if (result.Succeeded)
            SetSuccess("Ticket emails and the receipt are queued again.");
        else
            SetError(result.Refusal!);
        return RedirectToAction(nameof(Order), new { id });
    }

    /// <summary>
    /// Read-only, and open to Read (the Board) on purpose: checking the money is oversight. Dates are whole days in the
    /// organisation's time zone, <paramref name="to"/> included; the default is the last 30 days.
    /// </summary>
    [HttpGet("Recon")]
    public async Task<IActionResult> Recon(string? platform, string? from, string? to, bool all, CancellationToken ct)
    {
        var zone = orgTimeZone.Zone;
        var today = clock.GetCurrentInstant().InZone(zone).Date;
        var toDate = ReconDate(to) ?? today;
        var fromDate = ReconDate(from) ?? toDate.PlusDays(-29);
        if (fromDate > toDate)
            (fromDate, toDate) = (toDate, fromDate);

        var inUse = await checkout.GetPlatformsInUseAsync(ct);
        var keys = string.IsNullOrWhiteSpace(platform) ? inUse : [platform];
        var start = fromDate.AtStartOfDayInZone(zone).ToInstant();
        var end = toDate.PlusDays(1).AtStartOfDayInZone(zone).ToInstant();
        var recons = new List<TicketRecon>();
        var payments = new Dictionary<string, TicketPaymentRecon>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (await checkout.ReconcilePlatformAsync(key, start, end, ct) is not { } recon)
                return NotFound();
            recons.Add(recon);
            if (await checkout.ReconcilePaymentsAsync(key, start, end, ct) is { } payment)
                payments[key] = payment;
        }

        return View("~/Views/Tickets/Admin/Sales/Recon.cshtml",
            new ReconViewModel(recons, payments, inUse, platform, fromDate, toDate, all, zone));
    }

    private static LocalDate? ReconDate(string? value) =>
        value is not null && LocalDatePattern.Iso.Parse(value) is { Success: true } parsed ? parsed.Value : null;

    private IReadOnlyList<PlatformInfo> PlatformsForDisplay =>
        checkout.Platforms.OrderBy(platform => platform.Key, StringComparer.Ordinal).ToList();

    private async Task<PlatformsViewModel> PlatformsModelAsync(CancellationToken ct) =>
        new(PlatformsForDisplay, await checkout.ListTicketingSettingsAsync(ct), await settings.GetAllEventSettingsAsync(ct), null);

    private async Task<SalesEditViewModel> EditModelAsync(SalesEditForm form, string? error, CancellationToken ct) =>
        new(form, error, await settings.GetAllEventSettingsAsync(ct), await grants.GetProgramSummariesAsync(ct), PlatformsForDisplay,
            orgTimeZone.Zone);
}

internal sealed record SalesAdminIndexViewModel(
    IReadOnlyList<TicketingSettings> AllSettings,
    IReadOnlyList<EventSettingsInfo> Events,
    IReadOnlyList<PlatformInfo> Platforms,
    LocalDate Today)
{
    public string EventName(Guid id) => Events.FirstOrDefault(e => e.Id == id)?.EventName ?? id.ToString();
}

internal sealed record PlatformsViewModel(
    IReadOnlyList<PlatformInfo> Platforms,
    IReadOnlyList<TicketingSettings> AllSettings,
    IReadOnlyList<EventSettingsInfo> Events,
    PlatformCheckResult? Check)
{
    public string EventName(Guid id) => Events.FirstOrDefault(e => e.Id == id)?.EventName ?? id.ToString();
}

/// <summary><c>Payments</c> holds a platform only where Humans can read its payment account.</summary>
internal sealed record ReconViewModel(
    IReadOnlyList<TicketRecon> Recons,
    IReadOnlyDictionary<string, TicketPaymentRecon> Payments,
    IReadOnlyList<string> PlatformsInUse,
    string? Platform,
    LocalDate From,
    LocalDate To,
    bool All,
    DateTimeZone Zone);

internal sealed record PlatformCheckResult(string Key, string? PlatformEventRef, IReadOnlyList<PlatformFinding> Findings);

internal sealed record SalesEditViewModel(
    SalesEditForm Form,
    string? Error,
    IReadOnlyList<EventSettingsInfo> Events,
    IReadOnlyList<GrantProgramSummary> Programs,
    IReadOnlyList<PlatformInfo> Platforms,
    DateTimeZone Zone);

/// <summary>
/// Amounts are euros and the VAT a percentage, posted as text and read with the invariant culture (number inputs send
/// a dot whatever the admin's language). A row left wholly blank is dropped, so an empty spare row is harmless.
/// </summary>
internal sealed class SalesEditForm
{
    public Guid EventSettingsId { get; set; }
    public string PlatformKey { get; set; } = string.Empty;
    public string? PlatformEventRef { get; set; }
    public TicketSalesMode Mode { get; set; }

    /// <summary>Local date and time in the organisation's time zone, as a datetime-local input posts it.</summary>
    public string? SalesCloseAt { get; set; }

    public int Capacity { get; set; } = 3000;
    public string? TicketVatPercent { get; set; } = "10";
    public List<WaveRow> Waves { get; set; } = [];
    public List<VipRow> VipLevels { get; set; } = [];
    public List<PriceRuleRow> PriceRules { get; set; } = [];
    public List<ChildTypeRow> ChildTypes { get; set; } = [];
    public List<DonationPurposeRow> DonationPurposes { get; set; } = [];
    public ResaleScheme Resale { get; set; } = ResaleScheme.QueueAndDirected;
    public bool PartnerTickets { get; set; } = true;
    public bool Donations { get; set; } = true;
    public bool MailingListOptIn { get; set; } = true;
    public bool CompanionTickets { get; set; } = true;
    public string? TermsFolder { get; set; }

    internal sealed class WaveRow
    {
        public string? Name { get; set; }
        public string? EndsOn { get; set; }
        public string? Price { get; set; }
    }

    internal sealed class VipRow
    {
        public string? Total { get; set; }
    }

    internal sealed class ChildTypeRow
    {
        public string? Name { get; set; }
        public int? UnderAge { get; set; }
        public string? Price { get; set; }
    }

    internal sealed class DonationPurposeRow
    {
        public string? Name { get; set; }
    }

    internal sealed class PriceRuleRow
    {
        public Guid? GrantProgramId { get; set; }
        public TicketPriceRuleKind Kind { get; set; }
        public string? Amount { get; set; }
        public string? Label { get; set; }
    }

    private static readonly LocalDatePattern DatePattern = LocalDatePattern.Iso;
    private static readonly LocalDateTimePattern CloseAtPattern = DateFormattingExtensions.PlacementDateTimePattern;

    public static SalesEditForm From(TicketingSettings? e, DateTimeZone zone) => e is null ? new() : new()
    {
        EventSettingsId = e.EventSettingsId,
        PlatformKey = e.PlatformKey,
        PlatformEventRef = e.PlatformEventRef,
        Mode = e.Mode,
        SalesCloseAt = e.SalesCloseAt is { } close ? CloseAtPattern.Format(close.InZone(zone).LocalDateTime) : null,
        Capacity = e.Capacity,
        TicketVatPercent = (e.TicketVatRate * 100m).ToString("0.##", CultureInfo.InvariantCulture),
        Waves = e.Waves.Select(w => new WaveRow { Name = w.Name, EndsOn = DatePattern.Format(w.EndsOn), Price = Eur(w.PriceMinor) }).ToList(),
        VipLevels = e.VipTotalsMinor.Select(t => new VipRow { Total = Eur(t) }).ToList(),
        PriceRules = e.PriceRules.Select(r => new PriceRuleRow
        {
            GrantProgramId = r.GrantProgramId,
            Kind = r.Kind,
            Amount = r.Kind == TicketPriceRuleKind.WavePin ? null : Eur(r.AmountMinor),
            Label = r.Label,
        }).ToList(),
        ChildTypes = e.ChildTypes.Select(c => new ChildTypeRow { Name = c.Name, UnderAge = c.UnderAge, Price = Eur(c.PriceMinor) }).ToList(),
        DonationPurposes = (e.DonationPurposes ?? []).Select(p => new DonationPurposeRow { Name = p }).ToList(),
        Resale = e.EffectiveSwitches.Resale,
        PartnerTickets = e.EffectiveSwitches.PartnerTickets,
        Donations = e.EffectiveSwitches.Donations,
        MailingListOptIn = e.EffectiveSwitches.MailingListOptIn,
        CompanionTickets = e.EffectiveSwitches.CompanionTicketsOn,
        TermsFolder = e.TermsFolder,
    };

    public (TicketingSettingsSave? Save, string? Error) ToSave(Guid? id, DateTimeZone zone)
    {
        Instant? close = null;
        if (!string.IsNullOrWhiteSpace(SalesCloseAt))
        {
            var parsed = CloseAtPattern.Parse(SalesCloseAt.Trim());
            if (!parsed.Success) return (null, "Sales close needs a date and a time.");
            close = parsed.Value.InZoneLeniently(zone).ToInstant();
        }

        if (!decimal.TryParse(TicketVatPercent, NumberStyles.Number, CultureInfo.InvariantCulture, out var vatPercent))
            return (null, "Ticket VAT is a percentage, e.g. 10.");

        var waves = new List<TicketSalesWave>();
        foreach (var row in Waves.Where(r => !Blank(r.Name, r.EndsOn, r.Price)))
        {
            var name = row.Name?.Trim();
            if (string.IsNullOrEmpty(name)) return (null, "Every wave needs a name.");
            if (!DatePattern.Parse(row.EndsOn?.Trim() ?? string.Empty).TryGetValue(default, out var endsOn))
                return (null, $"Wave '{name}' needs a last day.");
            if (!TryMinor(row.Price, out var price)) return (null, $"Wave '{name}' needs a price.");
            waves.Add(new TicketSalesWave { Name = name, EndsOn = endsOn, PriceMinor = price });
        }

        var totals = new List<long>();
        foreach (var row in VipLevels.Where(r => !Blank(r.Total)))
        {
            if (!TryMinor(row.Total, out var total)) return (null, $"VIP total '{row.Total}' is not an amount.");
            totals.Add(total);
        }

        var rules = new List<TicketPriceRule>();
        foreach (var row in PriceRules.Where(r => r.GrantProgramId is not null || !Blank(r.Amount, r.Label)))
        {
            if (row.GrantProgramId is not { } programId) return (null, "Every price rule needs a grant program.");
            var label = row.Label?.Trim();
            if (string.IsNullOrEmpty(label)) return (null, "Every price rule needs the label the buyer sees.");
            long amount = 0;
            var optional = row.Kind == TicketPriceRuleKind.WavePin || (row.Kind == TicketPriceRuleKind.EarlyAccess && Blank(row.Amount));
            if (!optional && !TryMinor(row.Amount, out amount))
                return (null, $"Price rule '{label}' needs an amount.");
            rules.Add(new TicketPriceRule { GrantProgramId = programId, Kind = row.Kind, AmountMinor = amount, Label = label });
        }

        var childTypes = new List<TicketChildType>();
        foreach (var row in ChildTypes.Where(r => r.UnderAge is not null || !Blank(r.Name, r.Price)))
        {
            var name = row.Name?.Trim();
            if (string.IsNullOrEmpty(name)) return (null, "Every child ticket needs a name.");
            if (row.UnderAge is not { } underAge) return (null, $"Child ticket '{name}' needs an age.");
            if (!TryMinor(row.Price, out var price)) return (null, $"Child ticket '{name}' needs a price.");
            childTypes.Add(new TicketChildType { Name = name, UnderAge = underAge, PriceMinor = price });
        }

        var purposes = DonationPurposes.Where(r => !Blank(r.Name)).Select(r => r.Name!.Trim()).ToList();

        return (new TicketingSettingsSave(id, EventSettingsId, PlatformKey.Trim(), PlatformEventRef, Mode, close, Capacity,
            vatPercent / 100m, waves, totals, rules)
        {
            ChildTypes = childTypes,
            DonationPurposes = purposes,
            Switches = new TicketingSwitches
            {
                Resale = Resale,
                PartnerTickets = PartnerTickets,
                Donations = Donations,
                MailingListOptIn = MailingListOptIn,
                CompanionTickets = CompanionTickets,
            },
            TermsFolder = string.IsNullOrWhiteSpace(TermsFolder) ? null : TermsFolder.Trim().Trim('/'),
        }, null);
    }

    private static bool Blank(params string?[] values) => values.All(string.IsNullOrWhiteSpace);

    private static bool TryMinor(string? text, out long minor)
    {
        minor = 0;
        if (!decimal.TryParse(text?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var eur)) return false;
        var rounded = Math.Round(eur, 2, MidpointRounding.AwayFromZero);
        if (rounded < long.MinValue / 100m || rounded > long.MaxValue / 100m) return false;
        minor = (long)(rounded * 100m);
        return true;
    }

    private static string Eur(long minor) => (minor / 100m).ToString("0.##", CultureInfo.InvariantCulture);
}

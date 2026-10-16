using Humans.Base.Interfaces;
using Humans.Camps.Contracts;
using Humans.Settings.Contracts;
using Humans.Store.Data;
using Humans.Store.Domain;
using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.Store.Services;

/// <summary>Shared live prices and counterparty names for Store order views and issuance.</summary>
internal sealed class StoreOrderReader(
    IStoreRepository repo,
    ICampServiceRead campService,
    ITeamServiceRead teamService,
    IClock clock,
    ISettingsService settingsService) : IApplicationService
{
    /// <summary>Returns the active event's catalog year, falling back to the current UTC year before it exists.</summary>
    public async Task<int> GetCurrentEventYearAsync()
    {
        var activeEvent = await settingsService.GetActiveEventSettingsAsync();
        return activeEvent?.Year > 0 ? activeEvent.Year : clock.GetCurrentInstant().InUtc().Year;
    }

    /// <summary>
    /// Loads the current catalog price components (incl. deactivated products) for the active
    /// event's year, keyed by product id, so Open orders reprice to the live price. The org runs
    /// one event year, so a single catalog year drives repricing rather than each order's
    /// <c>Year</c> — which is also why legacy <c>store_orders</c> rows still at <c>Year = 0</c>
    /// reprice correctly.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, BalanceCalculator.ProductPrice>> LoadCurrentPricesAsync(
        CancellationToken ct)
    {
        var catalogYear = await GetCurrentEventYearAsync();
        var prices = new Dictionary<Guid, BalanceCalculator.ProductPrice>();
        foreach (var product in await repo.GetAllProductsForYearAsync(catalogYear, ct))
            prices[product.Id] = new BalanceCalculator.ProductPrice(
                product.UnitPriceEur, product.VatRatePercent, product.DepositAmountEur);
        return prices;
    }

    public async Task<string> ResolveCounterpartyDisplayNameAsync(Order o, CancellationToken ct)
    {
        if (o.TeamId is { } tid)
        {
            var team = await teamService.GetTeamAsync(tid, ct);
            return team?.Name ?? "(unknown team)";
        }
        if (o.CampSeasonId is { } sid)
        {
            var season = await campService.GetCampSeasonByIdAsync(sid, ct);
            return season?.Name ?? "(unknown camp)";
        }
        return "(unknown)";
    }
}

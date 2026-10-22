using Humans.Base.Interfaces;
using Humans.Camps.Contracts;
using Humans.Settings.Contracts;
using Humans.Store.Data;
using Humans.Store.Domain;
using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.Store.Services;

internal sealed class StoreOrderReader(
    IStoreRepository repo,
    ICampService campService,
    ITeamService teamService,
    IClock clock,
    ISettingsService settingsService) : IApplicationService
{
    /// <summary>Falls back to the current UTC year before an active event exists.</summary>
    public async Task<int> GetCurrentEventYearAsync(CancellationToken ct = default)
    {
        var activeEvent = await settingsService.GetActiveEventSettingsAsync(ct);
        return activeEvent?.Year > 0 ? activeEvent.Year : clock.GetCurrentInstant().InUtc().Year;
    }

    /// <summary>
    /// Includes deactivated products. The org runs one event year, so the active year's catalog drives repricing rather
    /// than each order's <c>Year</c>; that's also why legacy <c>Year = 0</c> rows reprice correctly.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, BalanceCalculator.ProductPrice>> LoadCurrentPricesAsync(
        CancellationToken ct)
    {
        var catalogYear = await GetCurrentEventYearAsync(ct);
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

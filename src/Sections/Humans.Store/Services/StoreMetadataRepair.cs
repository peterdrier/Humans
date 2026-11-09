using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Camps.Contracts;
using Humans.Store.Data;
using Humans.Store.Domain;
using Humans.Store.Services.Dtos;
using NodaTime;

namespace Humans.Store.Services;

internal sealed class StoreMetadataRepair(
    IStoreRepository repo,
    IAuditLogService audit,
    ICampService campService,
    IClock clock,
    ILogger logger) : IApplicationService
{
    internal async Task<OrderYearRepairReport> GetOrderYearRepairReportAsync(CancellationToken ct = default)
    {
        var orders = await repo.GetOrdersWithMissingYearAsync(ct);
        var rows = new List<OrderYearRepairRow>(orders.Count);
        foreach (var order in orders)
        {
            var season = order.CampSeasonId is { } seasonId
                ? await campService.GetCampSeasonByIdAsync(seasonId, ct)
                : null;
            rows.Add(new OrderYearRepairRow(
                order.Id,
                order.CampSeasonId,
                season?.Name,
                season?.Year));
        }

        return new OrderYearRepairReport(rows);
    }

    internal async Task<int> RepairOrderYearsAsync(Guid actorUserId, CancellationToken ct = default)
    {
        var orders = await repo.GetOrdersWithMissingYearAsync(ct);
        var repaired = 0;
        foreach (var order in orders)
        {
            if (await ResolveLegacyOrderYearAsync(order, actorUserId, nameof(RepairOrderYearsAsync), ct))
                repaired++;
        }

        return repaired;
    }

    internal async Task<IReadOnlyList<PaymentMethodRepairRow>> GetPaymentMethodRepairRowsAsync(CancellationToken ct = default)
    {
        var payments = await repo.GetPaymentsMissingMethodNameAsync(ct);
        return payments
            .Select(p => new PaymentMethodRepairRow(p.Id, p.OrderId, p.Method, p.AmountEur, p.ReceivedAt))
            .ToList();
    }

    /// <summary>Copies each legacy payment's int <see cref="Payment.Method"/> into <see cref="Payment.MethodName"/>, one audit entry per row.</summary>
    internal async Task<int> RepairPaymentMethodNamesAsync(Guid actorUserId, CancellationToken ct = default)
    {
        var payments = await repo.GetPaymentsMissingMethodNameAsync(ct);
        foreach (var payment in payments)
        {
            await repo.SetPaymentMethodNameAsync(payment.Id, payment.Method, ct);
            await audit.LogAsync(
                StoreAuditActions.StorePaymentMethodBackfilled, AuditEntityTypes.Payment, payment.Id,
                $"Copied payment method {payment.Method} into the string column",
                actorUserId, payment.OrderId, AuditEntityTypes.Order);
        }

        return payments.Count;
    }

    internal async Task<bool> ResolveLegacyOrderYearAsync(
        Order order,
        Guid actorUserId,
        string source,
        CancellationToken ct)
    {
        if (order.Year != 0 || order.CampSeasonId is not { } seasonId)
            return false;

        var season = await campService.GetCampSeasonByIdAsync(seasonId, ct);
        if (season is null)
            return false;

        order.Year = season.Year;
        order.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpdateOrderAsync(order, ct);
        var description = $"Resolved legacy store order year as {season.Year} from camp season {seasonId}";
        await audit.LogAsync(
            StoreAuditActions.StoreOrderYearBackfilled,
            AuditEntityTypes.Order,
            order.Id,
            description,
            actorUserId);
        logger.LogInformation(
            "Resolved legacy Store order {OrderId} year as {Year} from camp season {CampSeasonId} via {Source}",
            order.Id,
            season.Year,
            seasonId,
            source);
        return true;
    }
}

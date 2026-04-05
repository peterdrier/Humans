using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Web.Infrastructure;

public sealed record DevelopmentTicketSeedResult(
    int PaidOrders,
    int NonPaidOrders,
    int OrdersCreated,
    int AttendeesCreated,
    int PaidTicketsSold,
    decimal GrossRevenue,
    decimal DonationRevenue,
    decimal DiscountTotal,
    int OrdersWithDonation,
    int OrdersWithDiscountCode,
    int MatchedOrders,
    int MatchedAttendees,
    int TwoTicketOrders);

public sealed class DevelopmentTicketSeeder
{
    private const string DemoOrderPrefix = "dev-order-";
    private const string DemoTicketPrefix = "dev-ticket-";
    private const int TargetPaidTickets = 600;
    private const int TargetPaidRevenueEuros = 200000;
    private const int TwoTicketPaidOrders = 150;
    private const int SingleTicketPaidOrders = 300;

    private static readonly TicketTypeSeed[] PaidTicketMix =
    [
        new("Low income", 100m, 90),
        new("Contributor", 250m, 150),
        new("Standard", 275m, 210),
        new("VIP", 400m, 150)
    ];

    private static readonly decimal[] VipDonationOptions = [50m, 75m, 100m, 150m, 200m, 250m, 300m, 500m];
    private static readonly decimal[] ContributorDonationOptions = [25m, 35m, 50m, 75m, 100m, 150m, 200m, 250m];
    private static readonly decimal[] StandardDonationOptions = [10m, 15m, 20m, 25m, 35m, 50m, 75m, 100m];
    private static readonly decimal[] LowIncomeDonationOptions = [5m, 10m, 15m, 20m, 25m, 35m, 50m];

    private static readonly string[] FirstNames =
    [
        "Ariadna", "Mateo", "Lucia", "Pau", "Ines", "Jules", "Nora", "Leo", "Clara", "Hugo",
        "Noa", "Dario", "Marta", "Teo", "Sofia", "Bruno", "Laia", "Nico", "Mila", "Izan"
    ];

    private static readonly string[] LastNames =
    [
        "Soler", "Campos", "Navarro", "Torres", "Arias", "Costa", "Benet", "Ferrer", "Lopez", "Mora",
        "Sala", "Vidal", "Roig", "Pons", "Santos", "Prats", "Valle", "Luna", "Guasch", "Casals"
    ];

    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;
    private readonly TicketVendorSettings _settings;
    private readonly ILogger<DevelopmentTicketSeeder> _logger;

    public DevelopmentTicketSeeder(
        HumansDbContext dbContext,
        IClock clock,
        IOptions<TicketVendorSettings> settings,
        ILogger<DevelopmentTicketSeeder> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<DevelopmentTicketSeedResult> SeedAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var eventId = string.IsNullOrWhiteSpace(_settings.EventId) ? "dev-seeded-event" : _settings.EventId;
        var knownUsers = await LoadKnownUsersAsync(cancellationToken);

        await RemoveExistingDemoDataAsync(cancellationToken);

        var paidOrders = BuildPaidOrders(knownUsers, eventId);
        AdjustPaidRevenueToTarget(paidOrders, TargetPaidRevenueEuros);
        AssignPaidPurchaseDates(paidOrders, now.InUtc().Date.Year);
        var nonPaidOrders = BuildNonPaidOrders(knownUsers, eventId);

        var orderEntities = new List<TicketOrder>();
        var attendeeEntities = new List<TicketAttendee>();

        foreach (var plan in paidOrders.Concat(nonPaidOrders))
        {
            var orderId = Guid.NewGuid();
            var (stripeFee, applicationFee) = plan.PaymentStatus == TicketPaymentStatus.Paid
                ? ComputeFees(plan.TotalAmount, plan.PaymentMethod, plan.PaymentMethodDetail)
                : ((decimal?)null, (decimal?)null);

            var order = new TicketOrder
            {
                Id = orderId,
                VendorOrderId = plan.VendorOrderId,
                BuyerName = plan.BuyerName,
                BuyerEmail = plan.BuyerEmail,
                MatchedUserId = plan.MatchedBuyerUserId,
                TotalAmount = plan.TotalAmount,
                Currency = "EUR",
                DiscountCode = plan.DiscountCode,
                PaymentStatus = plan.PaymentStatus,
                VendorEventId = eventId,
                VendorDashboardUrl = $"https://demo.tickettailor.local/orders/{plan.VendorOrderId}",
                PurchasedAt = plan.PurchasedAt,
                SyncedAt = now,
                StripePaymentIntentId = plan.PaymentStatus == TicketPaymentStatus.Paid
                    ? $"pi_demo_{plan.OrderNumber:D6}"
                    : null,
                PaymentMethod = plan.PaymentMethod,
                PaymentMethodDetail = plan.PaymentMethodDetail,
                StripeFee = stripeFee,
                ApplicationFee = applicationFee,
                DiscountAmount = plan.DiscountAmount > 0 ? plan.DiscountAmount : null,
                DonationAmount = plan.DonationAmount,
                VatAmount = ComputeOrderVat(plan)
            };

            orderEntities.Add(order);

            for (var ticketIndex = 0; ticketIndex < plan.Attendees.Count; ticketIndex++)
            {
                var ticket = plan.Attendees[ticketIndex];
                attendeeEntities.Add(new TicketAttendee
                {
                    Id = Guid.NewGuid(),
                    VendorTicketId = $"{DemoTicketPrefix}{plan.OrderNumber:D4}-{ticketIndex + 1:D2}",
                    TicketOrderId = orderId,
                    AttendeeName = ticket.AttendeeName,
                    AttendeeEmail = ticket.AttendeeEmail,
                    MatchedUserId = ticket.MatchedUserId,
                    TicketTypeName = ticket.TicketTypeName,
                    Price = ticket.Price,
                    Status = ticket.Status,
                    VendorEventId = eventId,
                    SyncedAt = now
                });
            }
        }

        _dbContext.TicketOrders.AddRange(orderEntities);
        _dbContext.TicketAttendees.AddRange(attendeeEntities);

        var syncState = await _dbContext.TicketSyncStates.FindAsync([1], cancellationToken);
        if (syncState is not null)
        {
            syncState.VendorEventId = eventId;
            syncState.SyncStatus = TicketSyncStatus.Idle;
            syncState.LastError = null;
            syncState.LastSyncAt = now;
            syncState.StatusChangedAt = now;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var paidOrdersList = paidOrders.ToList();
        var paidTicketsSold = paidOrdersList.Sum(o => o.Attendees.Count(a => a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn));
        var grossRevenue = paidOrdersList.Sum(o => o.TotalAmount);
        var donationRevenue = paidOrdersList.Sum(o => o.DonationAmount);
        var discountTotal = paidOrdersList.Sum(o => o.DiscountAmount);
        var matchedOrders = paidOrdersList.Count(o => o.MatchedBuyerUserId.HasValue);
        var matchedAttendees = paidOrdersList.Sum(o => o.Attendees.Count(a => a.MatchedUserId.HasValue));

        _logger.LogInformation(
            "Development ticket seed completed: paidOrders={PaidOrders}, nonPaidOrders={NonPaidOrders}, ticketsSold={TicketsSold}, grossRevenue={GrossRevenue}",
            paidOrdersList.Count, nonPaidOrders.Count, paidTicketsSold, grossRevenue);

        return new DevelopmentTicketSeedResult(
            PaidOrders: paidOrdersList.Count,
            NonPaidOrders: nonPaidOrders.Count,
            OrdersCreated: orderEntities.Count,
            AttendeesCreated: attendeeEntities.Count,
            PaidTicketsSold: paidTicketsSold,
            GrossRevenue: grossRevenue,
            DonationRevenue: donationRevenue,
            DiscountTotal: discountTotal,
            OrdersWithDonation: paidOrdersList.Count(o => o.DonationAmount > 0),
            OrdersWithDiscountCode: paidOrdersList.Count(o => o.DiscountAmount > 0),
            MatchedOrders: matchedOrders,
            MatchedAttendees: matchedAttendees,
            TwoTicketOrders: paidOrdersList.Count(o => o.Attendees.Count == 2));
    }

    private async Task<List<KnownUserSeed>> LoadKnownUsersAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.UserEmails
            .Where(e => e.IsVerified)
            .OrderBy(e => e.Email)
            .Select(e => new KnownUserSeed(
                e.UserId,
                e.Email,
                e.User.DisplayName ?? e.User.Email ?? e.Email))
            .ToListAsync(cancellationToken);
    }

    private async Task RemoveExistingDemoDataAsync(CancellationToken cancellationToken)
    {
        var existingAttendees = await _dbContext.TicketAttendees
            .Where(a => EF.Functions.Like(a.VendorTicketId, $"{DemoTicketPrefix}%"))
            .ToListAsync(cancellationToken);

        if (existingAttendees.Count > 0)
        {
            _dbContext.TicketAttendees.RemoveRange(existingAttendees);
        }

        var existingOrders = await _dbContext.TicketOrders
            .Where(o => EF.Functions.Like(o.VendorOrderId, $"{DemoOrderPrefix}%"))
            .ToListAsync(cancellationToken);

        if (existingOrders.Count > 0)
        {
            _dbContext.TicketOrders.RemoveRange(existingOrders);
        }

        if (existingAttendees.Count > 0 || existingOrders.Count > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static List<PlannedOrder> BuildPaidOrders(
        IReadOnlyList<KnownUserSeed> knownUsers,
        string eventId)
    {
        var paidTicketPool = BuildPaidTicketPool(knownUsers);
        var orderSizes = Enumerable.Repeat(2, TwoTicketPaidOrders)
            .Concat(Enumerable.Repeat(1, SingleTicketPaidOrders))
            .Select((size, index) => new { size, SortKey = DeterministicGuid($"dev-paid-order-size:{index}:{size}") })
            .OrderBy(x => x.SortKey)
            .Select(x => x.size)
            .ToList();

        var orders = new List<PlannedOrder>(orderSizes.Count);
        var ticketCursor = 0;

        for (var orderIndex = 0; orderIndex < orderSizes.Count; orderIndex++)
        {
            var size = orderSizes[orderIndex];
            var attendees = paidTicketPool.Skip(ticketCursor).Take(size).ToList();
            ticketCursor += size;

            var matchedBuyer = orderIndex % 57 == 0 && knownUsers.Count > 0
                ? knownUsers[(orderIndex / 57) % knownUsers.Count]
                : null;

            var buyer = BuildBuyer(orderIndex, attendees, matchedBuyer);
            var (paymentMethod, paymentMethodDetail) = GetPaymentMethod(orderIndex);

            var order = new PlannedOrder
            {
                OrderNumber = orderIndex + 1,
                VendorOrderId = $"{DemoOrderPrefix}{orderIndex + 1:D4}",
                BuyerName = buyer.Name,
                BuyerEmail = buyer.Email,
                MatchedBuyerUserId = matchedBuyer?.UserId,
                PaymentStatus = TicketPaymentStatus.Paid,
                PurchasedAt = Instant.FromUtc(2026, 3, 14, 9, 0),
                PaymentMethod = paymentMethod,
                PaymentMethodDetail = paymentMethodDetail
            };

            order.Attendees.AddRange(attendees);

            if (ShouldApplyDiscount(orderIndex))
            {
                order.DiscountCode = BuildDiscountCode(orderIndex, attendees[0].TicketTypeName);
                order.DiscountAmount = GetDiscountAmount(attendees[0].TicketTypeName);
            }

            order.DonationAmount = GetInitialDonation(orderIndex, attendees);
            order.TotalAmount = ComputeOrderTotal(order.Attendees, order.DiscountAmount, order.DonationAmount);
            orders.Add(order);
        }

        return orders;
    }

    private static List<PlannedOrder> BuildNonPaidOrders(
        IReadOnlyList<KnownUserSeed> knownUsers,
        string eventId)
    {
        var templates = new (TicketPaymentStatus Status, string[] TicketTypes)[]
        {
            (TicketPaymentStatus.Pending, ["Standard"]),
            (TicketPaymentStatus.Pending, ["Contributor", "Standard"]),
            (TicketPaymentStatus.Pending, ["Low income"]),
            (TicketPaymentStatus.Pending, ["VIP"]),
            (TicketPaymentStatus.Refunded, ["Standard"]),
            (TicketPaymentStatus.Refunded, ["VIP", "Contributor"]),
            (TicketPaymentStatus.Refunded, ["Low income"]),
            (TicketPaymentStatus.Cancelled, ["Standard"]),
            (TicketPaymentStatus.Cancelled, ["Contributor"]),
            (TicketPaymentStatus.Cancelled, ["VIP", "Standard"])
        };

        var orders = new List<PlannedOrder>(templates.Length);

        for (var index = 0; index < templates.Length; index++)
        {
            var template = templates[index];
            var attendees = template.TicketTypes
                .Select((ticketType, ticketIndex) =>
                {
                    var price = GetTicketPrice(ticketType);
                    var person = BuildSyntheticPerson(700 + (index * 3) + ticketIndex, "guest");
                    return new PlannedTicket
                    {
                        TicketTypeName = ticketType,
                        Price = price,
                        SortKey = DeterministicGuid($"dev-nonpaid-ticket:{index}:{ticketIndex}:{ticketType}"),
                        AttendeeName = person.Name,
                        AttendeeEmail = person.Email,
                        Status = TicketAttendeeStatus.Void
                    };
                })
                .ToList();

            var buyerSeed = BuildSyntheticPerson(900 + index, "buyer");
            var buyer = index % 4 == 0 && knownUsers.Count > 0
                ? new BuyerSeed(knownUsers[index % knownUsers.Count].DisplayName, knownUsers[index % knownUsers.Count].Email)
                : new BuyerSeed(buyerSeed.Name, buyerSeed.Email);

            var order = new PlannedOrder
            {
                OrderNumber = SingleTicketPaidOrders + TwoTicketPaidOrders + index + 1,
                VendorOrderId = $"{DemoOrderPrefix}{SingleTicketPaidOrders + TwoTicketPaidOrders + index + 1:D4}",
                BuyerName = buyer.Name,
                BuyerEmail = buyer.Email,
                MatchedBuyerUserId = index % 4 == 0 && knownUsers.Count > 0 ? knownUsers[index % knownUsers.Count].UserId : null,
                PaymentStatus = template.Status,
                PurchasedAt = GetNonPaidPurchaseInstant(index),
                PaymentMethod = index % 2 == 0 ? "card" : null,
                PaymentMethodDetail = index % 2 == 0 ? "visa" : null
            };

            order.Attendees.AddRange(attendees);
            if (index % 3 == 0)
            {
                order.DiscountCode = BuildDiscountCode(800 + index, attendees[0].TicketTypeName);
                order.DiscountAmount = GetDiscountAmount(attendees[0].TicketTypeName);
            }

            order.TotalAmount = ComputeOrderTotal(order.Attendees, order.DiscountAmount, 0m);
            orders.Add(order);
        }

        return orders;
    }

    private static List<PlannedTicket> BuildPaidTicketPool(IReadOnlyList<KnownUserSeed> knownUsers)
    {
        var pool = new List<PlannedTicket>(TargetPaidTickets);

        foreach (var seed in PaidTicketMix)
        {
            for (var i = 0; i < seed.Count; i++)
            {
                pool.Add(new PlannedTicket
                {
                    TicketTypeName = seed.Name,
                    Price = seed.Price,
                    Status = TicketAttendeeStatus.Valid,
                    SortKey = DeterministicGuid($"dev-ticket-pool:{seed.Name}:{i}")
                });
            }
        }

        pool = pool
            .OrderBy(t => t.SortKey)
            .ToList();

        var matchedAttendeeCount = Math.Min(4, knownUsers.Count);
        for (var i = 0; i < matchedAttendeeCount; i++)
        {
            pool[i].AttendeeName = knownUsers[i].DisplayName;
            pool[i].AttendeeEmail = knownUsers[i].Email;
            pool[i].MatchedUserId = knownUsers[i].UserId;
        }

        for (var i = matchedAttendeeCount; i < pool.Count; i++)
        {
            var person = BuildSyntheticPerson(i + 1, i % 5 == 0 ? "crew" : "guest");
            pool[i].AttendeeName = person.Name;
            pool[i].AttendeeEmail = (i % 17 == 0 || i % 29 == 0) ? null : person.Email;
        }

        return pool;
    }

    private static BuyerSeed BuildBuyer(
        int orderIndex,
        IReadOnlyList<PlannedTicket> attendees,
        KnownUserSeed? matchedBuyer)
    {
        if (matchedBuyer is not null)
        {
            return new BuyerSeed(matchedBuyer.DisplayName, matchedBuyer.Email);
        }

        if (attendees.Count == 1 && !string.IsNullOrWhiteSpace(attendees[0].AttendeeEmail))
        {
            return new BuyerSeed(attendees[0].AttendeeName, attendees[0].AttendeeEmail!);
        }

        if (attendees.Count > 1 && orderIndex % 4 != 0 && !string.IsNullOrWhiteSpace(attendees[0].AttendeeEmail))
        {
            return new BuyerSeed(attendees[0].AttendeeName, attendees[0].AttendeeEmail!);
        }

        var person = BuildSyntheticPerson(400 + orderIndex, "buyer");
        return new BuyerSeed(person.Name, person.Email);
    }

    private static bool ShouldApplyDiscount(int orderIndex) => orderIndex % 11 == 0;

    private static string BuildDiscountCode(int orderIndex, string ticketTypeName)
    {
        var prefix = ticketTypeName switch
        {
            "Low income" => "SOLIDARITY",
            "Contributor" => "CREW",
            "Standard" => "COMMUNITY",
            "VIP" => "PATRON",
            _ => "DEMO"
        };

        return $"{prefix}-2026-{orderIndex + 1:D3}";
    }

    private static decimal GetDiscountAmount(string ticketTypeName) => ticketTypeName switch
    {
        "Low income" => 15m,
        "Contributor" => 30m,
        "Standard" => 40m,
        "VIP" => 50m,
        _ => 20m
    };

    private static decimal GetInitialDonation(int orderIndex, IReadOnlyList<PlannedTicket> attendees)
    {
        var containsVip = attendees.Any(a => string.Equals(a.TicketTypeName, "VIP", StringComparison.Ordinal));
        var containsContributor = attendees.Any(a => string.Equals(a.TicketTypeName, "Contributor", StringComparison.Ordinal));
        var containsStandard = attendees.Any(a => string.Equals(a.TicketTypeName, "Standard", StringComparison.Ordinal));

        if (containsVip && orderIndex % 2 == 0)
        {
            return VipDonationOptions[(orderIndex / 2) % VipDonationOptions.Length];
        }

        if (containsContributor && orderIndex % 3 == 0)
        {
            return ContributorDonationOptions[(orderIndex / 3) % ContributorDonationOptions.Length];
        }

        if (containsStandard && orderIndex % 4 == 0)
        {
            return StandardDonationOptions[(orderIndex / 4) % StandardDonationOptions.Length];
        }

        if (orderIndex % 7 == 0)
        {
            return LowIncomeDonationOptions[(orderIndex / 7) % LowIncomeDonationOptions.Length];
        }

        return 0m;
    }

    private static void AdjustPaidRevenueToTarget(List<PlannedOrder> orders, decimal targetGrossRevenue)
    {
        var currentRevenue = orders.Sum(o => o.TotalAmount);
        var diff = targetGrossRevenue - currentRevenue;

        if (diff < 0)
        {
            throw new InvalidOperationException(
                $"Ticket seed overshot the gross revenue target by {Math.Abs(diff):N2}. Adjust the baseline mix first.");
        }

        var adjustableOrders = orders
            .OrderByDescending(o => o.Attendees.Any(a => string.Equals(a.TicketTypeName, "VIP", StringComparison.Ordinal)))
            .ThenByDescending(o => o.Attendees.Any(a => string.Equals(a.TicketTypeName, "Contributor", StringComparison.Ordinal)))
            .ThenByDescending(o => o.Attendees.Count)
            .ToList();

        foreach (var order in adjustableOrders)
        {
            if (diff <= 0)
            {
                break;
            }

            var remainingHeadroom = 500m - order.DonationAmount;
            if (remainingHeadroom <= 0)
            {
                continue;
            }

            var increment = Math.Min(remainingHeadroom, diff);
            order.DonationAmount += increment;
            order.TotalAmount = ComputeOrderTotal(order.Attendees, order.DiscountAmount, order.DonationAmount);
            diff -= increment;
        }

        if (diff != 0)
        {
            throw new InvalidOperationException($"Unable to hit ticket gross revenue target exactly. Remaining diff: {diff:N2}");
        }
    }

    private static decimal ComputeOrderTotal(
        IReadOnlyList<PlannedTicket> attendees,
        decimal discountAmount,
        decimal donationAmount)
    {
        var ticketTotal = attendees.Sum(a => a.Price);
        return Math.Round(ticketTotal - discountAmount + donationAmount, 2);
    }

    private static decimal ComputeOrderVat(PlannedOrder order)
    {
        if (order.PaymentStatus != TicketPaymentStatus.Paid)
        {
            return 0m;
        }

        var totalVat = order.Attendees
            .Where(a => a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn)
            .Sum(a => Math.Round(Math.Min(a.Price, TicketConstants.VipThresholdEuros) * TicketConstants.VatRate / (1 + TicketConstants.VatRate), 2));

        return Math.Round(totalVat, 2);
    }

    private static void AssignPaidPurchaseDates(List<PlannedOrder> orders, int seasonYear)
    {
        var firstSaleDate = new LocalDate(seasonYear, 3, 14);
        var finalSaleDate = new LocalDate(seasonYear, 4, 5);
        var dailyTicketQuotas = BuildDailyTicketQuotas(firstSaleDate, finalSaleDate, TargetPaidTickets);

        var cumulativeTickets = 0;
        foreach (var order in orders)
        {
            var soldTickets = order.Attendees.Count(a => a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn);
            var midpointTicket = cumulativeTickets + (soldTickets / 2m);
            var day = ResolveTicketSaleDate(midpointTicket, dailyTicketQuotas);
            order.PurchasedAt = CreatePurchaseInstant(day, order.OrderNumber);
            cumulativeTickets += soldTickets;
        }
    }

    private static List<DailyTicketQuota> BuildDailyTicketQuotas(
        LocalDate firstSaleDate,
        LocalDate finalSaleDate,
        int totalTickets)
    {
        var firstBurstTickets = (int)Math.Round(totalTickets * 0.40m, MidpointRounding.AwayFromZero);
        var secondBurstTickets = (int)Math.Round(totalTickets * 0.20m, MidpointRounding.AwayFromZero);
        var remainingTickets = totalTickets - firstBurstTickets - secondBurstTickets;

        var phaseOneDates = Enumerable.Range(0, 3).Select(offset => firstSaleDate.PlusDays(offset)).ToList();
        var phaseTwoDates = Enumerable.Range(3, 4).Select(offset => firstSaleDate.PlusDays(offset)).ToList();
        var phaseThreeDates = Enumerable.Range(7, Period.Between(firstSaleDate.PlusDays(7), finalSaleDate.PlusDays(1), PeriodUnits.Days).Days)
            .Select(offset => firstSaleDate.PlusDays(offset))
            .ToList();

        var quotas = new List<DailyTicketQuota>();
        quotas.AddRange(DistributeTicketsAcrossDates(firstBurstTickets, phaseOneDates));
        quotas.AddRange(DistributeTicketsAcrossDates(secondBurstTickets, phaseTwoDates));
        quotas.AddRange(DistributeTicketsAcrossDates(remainingTickets, phaseThreeDates));
        return quotas;
    }

    private static IEnumerable<DailyTicketQuota> DistributeTicketsAcrossDates(
        int totalTickets,
        IReadOnlyList<LocalDate> dates)
    {
        if (dates.Count == 0)
        {
            yield break;
        }

        var baseTickets = totalTickets / dates.Count;
        var remainder = totalTickets % dates.Count;

        for (var i = 0; i < dates.Count; i++)
        {
            yield return new DailyTicketQuota(
                dates[i],
                baseTickets + (i < remainder ? 1 : 0));
        }
    }

    private static LocalDate ResolveTicketSaleDate(decimal midpointTicket, IReadOnlyList<DailyTicketQuota> dailyTicketQuotas)
    {
        decimal runningTotal = 0;
        foreach (var quota in dailyTicketQuotas)
        {
            runningTotal += quota.TicketCount;
            if (midpointTicket < runningTotal)
            {
                return quota.Date;
            }
        }

        return dailyTicketQuotas[^1].Date;
    }

    private static Instant CreatePurchaseInstant(LocalDate date, int orderNumber)
    {
        var localTime = new LocalTime(9 + (orderNumber % 9), (orderNumber * 11) % 60);
        return date.At(localTime).InZoneStrictly(DateTimeZone.Utc).ToInstant();
    }

    private static (decimal StripeFee, decimal ApplicationFee) ComputeFees(
        decimal totalAmount,
        string? paymentMethod,
        string? paymentMethodDetail)
    {
        if (string.IsNullOrWhiteSpace(paymentMethod))
        {
            return (0m, 0m);
        }

        var (variableRate, fixedFee) = paymentMethod switch
        {
            "card" when string.Equals(paymentMethodDetail, "visa", StringComparison.OrdinalIgnoreCase) => (0.0145m, 0.25m),
            "card" when string.Equals(paymentMethodDetail, "mastercard", StringComparison.OrdinalIgnoreCase) => (0.0149m, 0.25m),
            "link" => (0.0150m, 0.25m),
            "ideal" => (0.0165m, 0.20m),
            "bancontact" => (0.0170m, 0.20m),
            "klarna" => (0.0240m, 0.35m),
            _ => (0.0150m, 0.25m)
        };

        var stripeFee = Math.Round((totalAmount * variableRate) + fixedFee, 2);
        var applicationFee = Math.Round(Math.Max(totalAmount * 0.03m, 0.30m), 2);
        return (stripeFee, applicationFee);
    }

    private static (string PaymentMethod, string? PaymentMethodDetail) GetPaymentMethod(int orderIndex) => (orderIndex % 12) switch
    {
        0 or 1 or 2 or 3 => ("card", "visa"),
        4 or 5 => ("card", "mastercard"),
        6 => ("link", null),
        7 => ("ideal", null),
        8 => ("bancontact", null),
        9 => ("klarna", null),
        _ => ("card", "visa")
    };

    private static Instant GetNonPaidPurchaseInstant(int index)
    {
        var date = new LocalDate(2026, 4, 1).PlusDays(index / 3);
        var time = new LocalTime(10 + (index % 6), (index * 13) % 60);
        return date.At(time).InZoneStrictly(DateTimeZone.Utc).ToInstant();
    }

    private static decimal GetTicketPrice(string ticketTypeName) => ticketTypeName switch
    {
        "Low income" => 100m,
        "Contributor" => 250m,
        "Standard" => 275m,
        "VIP" => 400m,
        _ => 0m
    };

    private static BuyerSeed BuildSyntheticPerson(int seed, string role)
    {
        var first = FirstNames[(seed * 7) % FirstNames.Length];
        var last = LastNames[(seed * 11) % LastNames.Length];
        var name = $"{first} {last}";
        var email = $"{Slugify(first)}.{Slugify(last)}.{role}.{seed:D4}@ticketseed.local";
        return new BuyerSeed(name, email);
    }

    private static string Slugify(string value) =>
        string.Concat(value
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit));

    private static Guid DeterministicGuid(string value)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }

    private sealed record TicketTypeSeed(string Name, decimal Price, int Count);
    private sealed record KnownUserSeed(Guid UserId, string Email, string DisplayName);
    private sealed record BuyerSeed(string Name, string Email);

    private sealed class PlannedOrder
    {
        public required int OrderNumber { get; init; }
        public required string VendorOrderId { get; init; }
        public required string BuyerName { get; init; }
        public required string BuyerEmail { get; init; }
        public Guid? MatchedBuyerUserId { get; init; }
        public required TicketPaymentStatus PaymentStatus { get; init; }
        public Instant PurchasedAt { get; set; }
        public string? DiscountCode { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal DonationAmount { get; set; }
        public decimal TotalAmount { get; set; }
        public string? PaymentMethod { get; init; }
        public string? PaymentMethodDetail { get; init; }
        public List<PlannedTicket> Attendees { get; } = [];
    }

    private sealed class PlannedTicket
    {
        public required string TicketTypeName { get; init; }
        public required decimal Price { get; init; }
        public required Guid SortKey { get; init; }
        public required TicketAttendeeStatus Status { get; init; }
        public string AttendeeName { get; set; } = string.Empty;
        public string? AttendeeEmail { get; set; }
        public Guid? MatchedUserId { get; set; }
    }

    private sealed record DailyTicketQuota(LocalDate Date, int TicketCount);
}

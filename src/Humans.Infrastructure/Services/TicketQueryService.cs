using Microsoft.EntityFrameworkCore;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

public class TicketQueryService : ITicketQueryService
{
    private readonly HumansDbContext _dbContext;

    public TicketQueryService(HumansDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<int> GetUserTicketCountAsync(Guid userId)
    {
        // First check by MatchedUserId (set during sync)
        var attendeeCount = await _dbContext.TicketAttendees.CountAsync(a =>
            a.MatchedUserId == userId &&
            (a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn));

        var buyerCount = await _dbContext.TicketOrders.CountAsync(o =>
            o.MatchedUserId == userId &&
            o.PaymentStatus == TicketPaymentStatus.Paid);

        if (attendeeCount > 0 || buyerCount > 0)
            return Math.Max(attendeeCount, buyerCount);

        // Fallback: check all verified user emails (case-insensitive)
        // ToUpper() translates to SQL UPPER() in EF/Npgsql — analyzer MA0011 is a false positive here
#pragma warning disable MA0011
        var userEmails = await _dbContext.Set<UserEmail>()
            .Where(e => e.UserId == userId && e.IsVerified)
            .Select(e => e.Email.ToUpper())
            .ToListAsync();

        if (userEmails.Count == 0)
            return 0;

        attendeeCount = await _dbContext.TicketAttendees.CountAsync(a =>
            a.AttendeeEmail != null &&
            userEmails.Contains(a.AttendeeEmail.ToUpper()) &&
            (a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn));

        if (attendeeCount > 0)
            return attendeeCount;

        buyerCount = await _dbContext.TicketOrders.CountAsync(o =>
            userEmails.Contains(o.BuyerEmail.ToUpper()) &&
            o.PaymentStatus == TicketPaymentStatus.Paid);
#pragma warning restore MA0011

        return buyerCount;
    }

    public async Task<HashSet<Guid>> GetUserIdsWithTicketsAsync()
    {
        var attendeeUserIds = await _dbContext.TicketAttendees
            .Where(a => a.MatchedUserId != null &&
                (a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn))
            .Select(a => a.MatchedUserId!.Value)
            .Distinct()
            .ToListAsync();

        var buyerUserIds = await _dbContext.TicketOrders
            .Where(o => o.MatchedUserId != null &&
                o.PaymentStatus == TicketPaymentStatus.Paid)
            .Select(o => o.MatchedUserId!.Value)
            .Distinct()
            .ToListAsync();

        return attendeeUserIds.Union(buyerUserIds).ToHashSet();
    }
}

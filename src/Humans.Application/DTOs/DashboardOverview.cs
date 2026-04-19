using NodaTime;

namespace Humans.Application.DTOs;

public record DashboardOverview(
    int TotalShifts,
    int FilledShifts,
    PeriodBreakdown PeriodFillRates,
    int TicketHolderCount,
    int TicketHoldersEngaged,
    int NonTicketSignups,
    int StalePendingCount,
    IReadOnlyList<DepartmentStaffingRow> Departments);

public record PeriodBreakdown(double BuildPct, double EventPct, double StrikePct);

public record DepartmentStaffingRow(
    Guid DepartmentId,
    string DepartmentName,
    int TotalShifts,
    int FilledShifts,
    int SlotsRemaining,
    PeriodStaffing Build,
    PeriodStaffing Event,
    PeriodStaffing Strike,
    IReadOnlyList<SubgroupStaffingRow> Subgroups);

public record SubgroupStaffingRow(
    Guid? TeamId,
    string Name,
    bool IsDirect,
    int TotalShifts,
    int FilledShifts,
    int SlotsRemaining,
    PeriodStaffing Build,
    PeriodStaffing Event,
    PeriodStaffing Strike);

public record PeriodStaffing(int Total, int Filled, int SlotsRemaining);

public record CoordinatorActivityRow(
    Guid TeamId,
    string TeamName,
    IReadOnlyList<CoordinatorLogin> Coordinators,
    int PendingSignupCount);

public record CoordinatorLogin(Guid UserId, string DisplayName, Instant? LastLoginAt);

public record DashboardTrendPoint(
    LocalDate Date,
    int NewSignups,
    int NewTicketSales,
    int DistinctLogins);

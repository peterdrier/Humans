using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Domain;
using NodaTime;

namespace Humans.Preapprovals.Services;

internal sealed record ProgramInfo(
    Guid Id,
    string Name,
    string? Description,
    ProgramKind Kind,
    decimal? PerPersonAmount,
    decimal? TotalCeiling,
    int HoldedAccountNumber,
    string HoldedAccountId,
    bool AcceptsApplications,
    LocalDate ClaimBy,
    ProgramStatus Status,
    Guid CreatedByUserId,
    Instant CreatedAt,
    Instant UpdatedAt,
    IReadOnlyList<Guid> ManagerUserIds,
    IReadOnlyList<GrantInfo> Grants)
{
    /// <summary>What counts against the ceiling.</summary>
    public decimal Granted => Grants.Where(g => g.CountsAgainstCeiling).Sum(g => g.MaxAmount);

    public decimal Consumed => Grants.Where(g => g.Status == GrantStatus.Consumed).Sum(g => g.MaxAmount);

    /// <summary>The Applied queue a manager sees beside what is committed.</summary>
    public decimal Requested => Grants.Where(g => g.Status == GrantStatus.Applied).Sum(g => g.MaxAmount);

    /// <summary>Revoked, plus Open past the deadline. Frozen rows, never adjusted.</summary>
    public decimal NotClaimed(LocalDate today) => Grants
        .Where(g => g.Status == GrantStatus.Revoked || (g.Status == GrantStatus.Open && today > ClaimBy))
        .Sum(g => g.MaxAmount);

    public decimal? Remaining => TotalCeiling is { } ceiling ? ceiling - Granted : null;

    public int OpenApplications => Grants.Count(g => g.Status == GrantStatus.Applied);

    public bool IsManager(Guid userId) => ManagerUserIds.Contains(userId);

    public bool DeadlinePassed(LocalDate today) => today > ClaimBy;

    /// <summary>The rule <c>UpdateProgramAsync</c> enforces.</summary>
    public bool KindLocked => Grants.Count > 0;

    /// <summary>The apply form shows and the POST is accepted only while all three hold.</summary>
    public bool IsAcceptingApplications(LocalDate today) =>
        Status == ProgramStatus.Open && AcceptsApplications && !DeadlinePassed(today);
}

internal sealed record GrantInfo(
    Guid Id,
    Guid ProgramId,
    string ProgramName,
    ProgramKind ProgramKind,
    Guid BeneficiaryUserId,
    string? Label,
    decimal MaxAmount,
    GrantStatus Status,
    string? ApplicationNote,
    decimal? RequestedAmount,
    Guid? DecidedByUserId,
    Instant? DecidedAt,
    Guid? ExpenseReportId,
    LocalDate ClaimBy,
    Instant CreatedAt,
    Instant UpdatedAt)
{
    public bool CountsAgainstCeiling =>
        Status is GrantStatus.Open or GrantStatus.Claimed or GrantStatus.Consumed;

    public bool IsTerminal =>
        Status is GrantStatus.Consumed or GrantStatus.Declined or GrantStatus.Revoked;

    /// <summary>The only state a member can file against.</summary>
    public bool IsClaimable(LocalDate today) => Status == GrantStatus.Open && today <= ClaimBy;

    public bool DeadlinePassed(LocalDate today) => Status == GrantStatus.Open && today > ClaimBy;

    public int DaysLeft(LocalDate today) => Period.Between(today, ClaimBy, PeriodUnits.Days).Days;
}

/// <summary>What the program form posts. Managers are the full desired set.</summary>
internal sealed record ProgramSave(
    string Name,
    string? Description,
    ProgramKind Kind,
    decimal? PerPersonAmount,
    decimal? TotalCeiling,
    int HoldedAccountNumber,
    bool AcceptsApplications,
    LocalDate ClaimBy,
    ProgramStatus Status,
    IReadOnlyList<Guid> ManagerUserIds);

internal sealed record PreapprovalRefusal(string Key, params object[] Args);

internal record PreapprovalMutationResult(PreapprovalRefusal? Refusal = null)
{
    public static readonly PreapprovalMutationResult Ok = new();
}

internal sealed record PreapprovalMutationResult<T>(T? Value = default, PreapprovalRefusal? Refusal = null)
    : PreapprovalMutationResult(Refusal);

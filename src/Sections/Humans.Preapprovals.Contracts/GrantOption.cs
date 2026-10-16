using NodaTime;

namespace Humans.Preapprovals.Contracts;

/// <summary>
/// A grant as the rest of the app sees it: enough to list it on an expense form and to stamp
/// a report. Nothing outside Preapprovals learns what a program is beyond its name.
/// </summary>
public sealed record GrantOption(
    Guid Id,
    string ProgramName,
    string? Label,
    decimal MaxAmount,
    int HoldedAccountNumber,
    string HoldedAccountId,
    LocalDate ClaimBy,
    GrantStatus Status);

/// <summary>
/// What <see cref="IPreapprovalService.ClaimAsync"/> returns and Expenses writes onto the
/// report: the cap and account read from the program as it is at submit time.
/// </summary>
public sealed record GrantStamp(
    decimal MaxAmount,
    int HoldedAccountNumber,
    string HoldedAccountId,
    LocalDate ClaimBy);

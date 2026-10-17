using Humans.Preapprovals.Domain;
using NodaTime;

namespace Humans.Preapprovals.Services;

/// <summary>
/// A grant as the section's expense-facing reads see it: enough to list it on an expense form and to stamp
/// a report. Nothing outside Preapprovals learns what a program is beyond its name.
/// </summary>
internal sealed record GrantOption(
    Guid Id,
    string ProgramName,
    string? Label,
    decimal MaxAmount,
    int HoldedAccountNumber,
    string HoldedAccountId,
    LocalDate ClaimBy,
    GrantStatus Status);

/// <summary>
/// What <see cref="IPreapprovalManagement.ClaimAsync"/> returns and Expenses writes onto the
/// report: the cap and account read from the program as it is at submit time.
/// </summary>
internal sealed record GrantStamp(
    decimal MaxAmount,
    int HoldedAccountNumber,
    string HoldedAccountId,
    LocalDate ClaimBy);

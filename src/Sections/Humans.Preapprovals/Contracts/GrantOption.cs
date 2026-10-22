using NodaTime;

namespace Humans.Preapprovals.Contracts;

/// <summary>Nothing outside Preapprovals learns what a program is beyond its name.</summary>
public sealed record GrantOption(
    Guid Id,
    string ProgramName,
    string? Label,
    decimal MaxAmount,
    int HoldedAccountNumber,
    string HoldedAccountId,
    LocalDate ClaimBy,
    GrantStatus Status);

/// <summary>The cap and account as the program has them at submit time; Expenses writes them onto the report.</summary>
public sealed record GrantStamp(
    decimal MaxAmount,
    int HoldedAccountNumber,
    string HoldedAccountId,
    LocalDate ClaimBy);

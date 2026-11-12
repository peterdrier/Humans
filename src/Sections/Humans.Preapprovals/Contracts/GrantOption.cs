using NodaTime;

namespace Humans.Preapprovals.Contracts;

/// <summary>
/// Nothing outside Preapprovals learns what a program is beyond its name and managers. <see cref="ClaimableToday"/>
/// is Open with the claim deadline not yet passed (Europe/Madrid).
/// </summary>
public sealed record GrantOption(
    Guid Id,
    Guid HolderUserId,
    IReadOnlyList<Guid> ManagerUserIds,
    string ProgramName,
    string? Label,
    decimal MaxAmount,
    int HoldedAccountNumber,
    string HoldedAccountId,
    LocalDate ClaimBy,
    GrantStatus Status,
    bool ClaimableToday);

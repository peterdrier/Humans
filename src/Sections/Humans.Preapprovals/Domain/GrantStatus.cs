namespace Humans.Preapprovals.Contracts;

/// <summary>
/// Where one grant is in its life. <c>Applied</c> is the application; <c>Open</c> is the only
/// state an expense report can be filed against; <c>Claimed</c> holds it while that report is in
/// flight; <c>Consumed</c>, <c>Declined</c> and <c>Revoked</c> are terminal. There is no expired
/// state: a grant past its program's claim deadline stays <c>Open</c> and is simply not claimable
/// (docs/features/global/preapprovals.md).
/// </summary>
public enum GrantStatus
{
    Applied = 0,
    Open = 1,
    Claimed = 2,
    Consumed = 3,
    Declined = 4,
    Revoked = 5,
}

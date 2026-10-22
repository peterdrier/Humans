namespace Humans.Preapprovals.Contracts;

/// <summary>
/// Only <c>Open</c> can be filed against; <c>Claimed</c> holds it while that report is in flight. There is no expired
/// state: a grant past its program's claim deadline stays <c>Open</c> and is simply not claimable.
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

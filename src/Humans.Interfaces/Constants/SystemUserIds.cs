namespace Humans.Domain.Constants;

/// <summary>
/// Well-known IDs for system accounts that are not real humans.
/// </summary>
public static class SystemUserIds
{
    // Reserved GUID block: 0004. See docs/guid-reservations.md.

    /// <summary>
    /// The shared gate-terminal account: the laptop at gate signs in with this
    /// account (username + password set from the ticketing admin page) to use the
    /// read-only ticket lookup at /Scanner/Tickets. It is not a person — it holds
    /// no roles, no email, and only the ScannerAccess policy admits it by id.
    /// </summary>
    public static readonly Guid GateTerminal = Guid.Parse("00000000-0000-0000-0004-000000000001");

    /// <summary>Username gate staff type on /Account/GateLogin.</summary>
    public const string GateTerminalLoginName = "gate";

    /// <summary>Display name of the gate-terminal account.</summary>
    public const string GateTerminalDisplayName = "Gate Terminal";
}

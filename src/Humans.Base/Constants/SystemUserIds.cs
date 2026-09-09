namespace Humans.Base.Constants;

/// <summary>
/// Well-known IDs for system accounts that are not real humans.
/// </summary>
public static class SystemUserIds
{
    // Reserved GUID block: 0004. See docs/guid-reservations.md.

    /// <summary>
    /// The shared gate-terminal account: the laptop at gate signs in with this
    /// account (username + password set from the ticketing admin page) to run the
    /// Gate admissions terminal at /Gate. It is not a person — it holds no roles and
    /// no email; the ScannerAccess and GateAdmit policies admit it by id.
    /// </summary>
    public static readonly Guid GateTerminal = Guid.Parse("00000000-0000-0000-0004-000000000001");

    /// <summary>Username gate staff type on /Account/GateLogin.</summary>
    public const string GateTerminalLoginName = "gate";

    /// <summary>Display name of the gate-terminal account.</summary>
    public const string GateTerminalDisplayName = "Gate Terminal";
}

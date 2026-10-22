namespace Humans.Tickets;

/// <summary>
/// Marker type for Tickets' resource set. The <c>.resx</c> files sit beside this file on
/// purpose: the SDK derives the manifest name from the adjacent same-named <c>.cs</c>
/// file's namespace, not from the folder path, so this must stay
/// <c>namespace Humans.Tickets</c> — <c>Humans.Tickets.Resources</c> would make every
/// string in the set fall back to its raw key at runtime.
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers via
/// <c>GetExportedTypes()</c>; an internal marker is skipped in silence (§15.3b).
/// Section-owned keys use <c>Tickets_</c>; the transfer wizard uses
/// <c>Tickets_TicketTransfer_*</c>.
/// <para>
/// Keys that look like this section's and are <em>not</em> here stayed behind because their
/// renderer is Shell's: <c>Dashboard_Ticket*</c>, <c>Guest_Tickets*</c>, <c>Home_Tickets*</c>,
/// <c>TeamAdmin_TicketLabel</c>, <c>ShiftDash_*Ticket*</c>, <c>GateLogin_Description</c> and
/// <c>Users_EmailGrid_DeleteTicketLinkedBlocked</c> (§15 step 3b, carve by renderer).
/// </para>
/// </remarks>
public class TicketsResource;

namespace Humans.Tickets;

/// <summary>
/// Marker type for Tickets' resource set. The <c>.resx</c> files sit beside this file on
/// purpose: the SDK derives the manifest name from the adjacent same-named <c>.cs</c>
/// file's namespace, not from the folder path, so this must stay
/// <c>namespace Humans.Tickets</c> — <c>Humans.Tickets.Resources</c> would make every
/// string in the set fall back to its raw key at runtime (design §3).
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers via
/// <c>GetExportedTypes()</c>; an internal marker is skipped in silence (§15.3b).
/// The set is the 25 <c>TicketTransfer_*</c> keys read by the transfer wizard — the only
/// view in the section that localizes anything — plus the four
/// <c>Enum_TicketTransferStatus_*</c> keys, which have no call site (the wizard spells the
/// four statuses with its own <c>TicketTransfer_Status*</c> keys) and moved with the set
/// rather than being stranded in <c>SharedResource</c> or deleted, since deleting dead copy
/// is behavioural. The admin ticket views carry no <c>Localizer[…]</c> call at all.
/// <para>
/// Keys that look like this section's and are <em>not</em> here stayed behind because their
/// renderer is Shell's: <c>Dashboard_Ticket*</c>, <c>Guest_Tickets*</c>, <c>Home_Tickets*</c>,
/// <c>TeamAdmin_TicketLabel</c>, <c>ShiftDash_*Ticket*</c>, <c>GateLogin_Description</c> and
/// <c>EmailGrid_DeleteTicketLinkedBlocked</c> (§15 step 3b, carve by renderer).
/// </para>
/// </remarks>
public class TicketsResource;
